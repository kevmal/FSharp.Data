﻿// --------------------------------------------------------------------------------------
// Utilities for transforming F# quotations to reference types from different assemblies
// --------------------------------------------------------------------------------------

namespace ProviderImplementation

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.ExprShape
open Microsoft.FSharp.Quotations.Patterns
open Microsoft.FSharp.Reflection
open ProviderImplementation.ProvidedTypes

//type DT = class end
type TypeRT = { X: Type }
type ExprRT = { X: Expr }
type VarRT = { X: Var }

[<AutoOpen>]
module QUtils = 
    let rawtype (t:TypeRT) = t.X
    let rawexpr (t:ExprRT) = t.X
    let rawvar (t:VarRT) = t.X
    let rttype (t:Type) : TypeRT = { X = t }
    let rtexpr (t:Expr) : ExprRT = { X = t }
    let rtvar (t:Var) : VarRT = { X = t }
    let VarRT (nm,ty) = Var(nm, rawtype ty) |> rtvar

/// When we split a type provider into a runtime assembly and a design time assembly, we can no longer 
/// use quotations directly, because they will reference the wrong types. AssemblyReplacer fixes that 
/// by transforming the expressions generated by the quotations to have the right types. 
/// For the Provided* type InvokeCode and GetterCode, we to first transform the argument expressions
/// to the design time types, so we can splice it in the quotation, and then after that we have to convert
/// it back to the runtime type. A further complication arises because Expr.Var's have reference equality, so
/// when can't just create new Expr.Var's with the same variable name and a different type. When transforming
/// them from runtime to design time we keep them in a dictionary, so that when we convert them back to runtime
/// we can return the exact same instance that was provided to us initially.
/// Another limitation (not only of this method, but in general with type providers) is that we can never use 
/// expressions that use F# functions as parameters or return values, we always have to use felegates instead.
type AssemblyReplacer(designTimeAssemblies, referencedAssemblies) =

  /// Creates an AssemblyReplacer with the provided list of designTimeAssembly*runtimeAssembly mappings
  /// Should always receive the current running assembly paired with the RuntimeAssembly from TypeProviderConfig,
  /// and in case we're targetting a different .Net framework profile, the FSharp.Core version referenced
  /// by the current assembly paired with the FSharp.Core version referenced by the runtime assembly
  let varTable = Dictionary<Var, Var>()
  let typeCacheFwd = Dictionary<Type, Type>()
  let typeCacheBwd = Dictionary<Type, Type>()

  let fixName (fullName:string) =
      if fullName.StartsWith("FSI_") then 
          // when F# Interactive is the host of the design time assembly,
          // all namespaces are prefixed with FSI_, in the runtime assembly
          // the name won't have that prefix
          fullName.Substring(fullName.IndexOf('.') + 1)
      else 
          fullName

  let tryGetTypeFromAssembly fullName (asm:Assembly) =

        if asm.FullName.StartsWith "FSI-ASSEMBLY" then
            // when F# Interactive is the host of the design time assembly,
            // for each type in the runtime assembly there might be multiple
            // versions (FSI_0001.FullTypeName, FSI_0002.FullTypeName, etc).
            // Get the last one.
            asm.GetTypes() 
            |> Seq.filter (fun t -> fixName t.FullName = fullName)
            |> Seq.sortBy (fun t -> t.FullName)
            |> Seq.toList
            |> function [] -> None | xs -> Some (Seq.last xs, false)
        else
            asm.GetType fullName |> function null -> None | x -> Some (x, true)


  let replaceTypeDefinition fwd (t:Type) =
      let cache = (if fwd then typeCacheFwd else typeCacheBwd)
      match cache.TryGetValue(t) with
      | true, newT -> newT
      | false, _ -> 
            let asms = (if fwd then referencedAssemblies else designTimeAssemblies)
            let fullName = fixName t.FullName

            // For normal type provider hosts like fsc.exe System.Void is a special case and these
            // hosts expect it to always be provided as [FSharp.Core 4.4.0.0]typeof<System.Void>.  
            // This is really a mistake in ExtensionTyping.fs in the F# compiler which calls 
            // typeof<System.Void>.Equals(ty).
            if fullName = "System.Void" then typeof<System.Void> else

            match Array.choose (tryGetTypeFromAssembly fullName) asms |> Seq.distinct |> Seq.toArray with
            | [| (newT, canSave) |] -> 
                 if canSave then cache.[t] <- newT
                 //printfn "%s %A --> %A" (if fwd then "fwd" else "bwd") t newT
                 newT
            | r when r.Length > 0 -> 
                let msg = 
                    if fwd then sprintf "The type '%O' utilized by a type provider was found in multiple assemblies in the reference assembly set '%A'. You may need to adjust your assembly references to avoid ambiguities." t referencedAssemblies
                    else sprintf "The type '%O' utilized by a type provider was not found in the assembly set '%A' used by the type provider itself. Please report this problem to the project site for the type provider." t designTimeAssemblies
                failwith msg
            | _ -> 
                let msg = 
                    if fwd then sprintf "The type '%O' utilized by a type provider was not found in reference assembly set '%A'. You may be referencing a portable profile which contains fewer types than those needed by the type provider you are using." t referencedAssemblies
                    else sprintf "The runtime-time type '%O' utilized by a type provider was not found in the compilation-time assembly set '%A'. You may be referencing a portable profile which contains fewer types than those needed by the type provider you are using. Please report this problem to the project site for the type provider." t designTimeAssemblies
                failwith msg


  let rec replaceType fwd (t:Type) =
      if t :? ProvidedTypeDefinition then t
      // Don't try to translate F# abbreviations
      elif t :? ProvidedSymbolType && (t :?> ProvidedSymbolType).IsFSharpTypeAbbreviation then t
      elif t.IsGenericType && not t.IsGenericTypeDefinition then 
          let genericType = t.GetGenericTypeDefinition()
          let newT = replaceTypeDefinition fwd genericType
          let typeArguments = t.GetGenericArguments() |> Array.map (replaceType fwd)
          newT.MakeGenericType(typeArguments)
             
      elif t.IsGenericParameter then t
      elif t.IsArray || t.IsByRef || t.IsPointer then 
          let elemType = t.GetElementType()
          let elemTypeT = replaceType fwd elemType
          if t.IsArray then 
              let rank = t.GetArrayRank()
              if rank = 1 then elemTypeT.MakeArrayType() else elemTypeT.MakeArrayType(t.GetArrayRank())
          elif t.IsByRef then elemTypeT.MakeByRefType()
          else elemTypeT.MakePointerType()

      else 
          replaceTypeDefinition fwd t

  let replaceProperty fwd (p : PropertyInfo) =
    if p :? ProvidedProperty then p
    else 
      let t = replaceType fwd p.DeclaringType
      let isStatic = 
        p.CanRead && p.GetGetMethod().IsStatic || 
        p.CanWrite && p.GetSetMethod().IsStatic
      let bindingFlags = 
        BindingFlags.Public ||| BindingFlags.NonPublic ||| 
          (if isStatic then BindingFlags.Static else BindingFlags.Instance)
      let newP = t.GetProperty(p.Name, bindingFlags)
      if newP = null then
        failwithf "Property '%O' of type '%O' not found" p t 
      newP

  let replaceField fwd (f : FieldInfo) =
    if f :? ProvidedField then f
    else 
      let t = replaceType fwd f.DeclaringType
      let bindingFlags = 
        (if f.IsPublic then BindingFlags.Public else BindingFlags.NonPublic) ||| 
        (if f.IsStatic then BindingFlags.Static else BindingFlags.Instance)
      let newF = t.GetField(f.Name, bindingFlags)
      if newF = null then failwithf "Field '%O' of type '%O' not found" f t 
      newF
  
  let replaceMethod fwd (m : MethodInfo) =
    if m :? ProvidedMethod then m
    else 
      let declTyT = replaceType fwd m.DeclaringType
      let mT =
          if m.IsGenericMethod then 
            let genericMethod = m.GetGenericMethodDefinition()
            let parameterTypesT = genericMethod.GetParameters() |> Array.map (fun p -> replaceType fwd p.ParameterType) 
            let genericMethodT = declTyT.GetMethod(genericMethod.Name,parameterTypesT)
            if genericMethodT = null then null else
            let typeArgumentsT =  m.GetGenericArguments() |> Array.map (replaceType fwd) 
            genericMethodT.MakeGenericMethod(typeArgumentsT)
          else 
            let parameterTypesT = m.GetParameters() |> Array.map (fun p -> replaceType fwd p.ParameterType) 
            declTyT.GetMethod(m.Name, parameterTypesT)
      match mT with 
      | null -> failwithf "Method '%O' not found in type '%O'" m mT 
      | _ -> 
          //printfn "%s method %A --> %A" (if fwd then "fwd" else "bwd") m mT; 
          mT

  let replaceConstructor fwd (cons : ConstructorInfo) =
    if cons :? ProvidedConstructor then cons
    else 
        let declTyT = replaceType fwd cons.DeclaringType
        let parameterTypesT = cons.GetParameters() |> Array.map (fun p -> replaceType fwd p.ParameterType) 
        let consT = declTyT.GetConstructor(parameterTypesT)
        match consT with 
        | null -> failwithf "Constructor '%O' not found in type '%O'" cons declTyT 
        | _ -> consT

  let replaceVar fwd (v: Var) =
    if v.Type :? ProvidedTypeDefinition then v
    else 
      let createNewVar() = 
        Var (v.Name, replaceType fwd v.Type, v.IsMutable)
      if fwd then
        match varTable.TryGetValue v with
        | true, v -> v
        | false, _ -> 
            // It's a variable local to the quotation
            let newVar = createNewVar()
            // store it so we reuse it from now on
            varTable.Add(v, newVar)
            newVar
      else
        let newVar = createNewVar()
        // store the original var as we'll have to revert to it later
        varTable.Add(newVar, v)
        newVar
  
  let rec replaceExpr fwd quotation =
    let rt = replaceType fwd 
    let rp = replaceProperty fwd 
    let rf = replaceField fwd 
    let rm = replaceMethod fwd 
    let rc = replaceConstructor fwd 
    let rv = replaceVar fwd 
    let re = replaceExpr fwd 
    
    match quotation with
    | Call (obj, m, args) -> 
        let mR = rm m
        let argsR = List.map re args
        match obj with
        | Some obj -> Expr.CallUnchecked (re obj, mR, argsR)
        | None -> Expr.CallUnchecked (mR, argsR)
    | PropertyGet (obj, p, indexArgs) -> 
        let pR = rp p
        let indexArgsR = List.map re indexArgs
        match obj with
        | Some obj -> Expr.PropertyGetUnchecked (re obj, pR, indexArgsR)
        | None -> Expr.PropertyGetUnchecked (pR, indexArgsR)
    | PropertySet (obj, p, indexArgs, value) -> 
        let pR = rp p
        let indexArgsR = List.map re indexArgs
        match obj with
        | Some obj -> Expr.PropertySetUnchecked (re obj, pR, re value, indexArgsR)
        | None -> Expr.PropertySetUnchecked (pR, re value, indexArgsR)
    | NewObject (c, exprs) ->
        let exprsR = List.map re exprs
        Expr.NewObjectUnchecked (rc c, exprsR)
    | Coerce (expr, t) ->
        Expr.Coerce (re expr, rt t)
    | NewArray (t, exprs) ->
        Expr.NewArrayUnchecked (rt t, List.map re exprs)
    | NewTuple (exprs) ->
        Expr.NewTuple (List.map re exprs)
    | TupleGet (expr, i) ->
        Expr.TupleGetUnchecked (re expr, i)
    | NewDelegate (t, vars, expr) ->
        Expr.NewDelegateUnchecked (rt t, List.map rv vars, re expr)
    | FieldGet (obj, f) -> 
        match obj with
        | Some obj -> Expr.FieldGetUnchecked (re obj, rf f)
        | None -> Expr.FieldGetUnchecked (rf f)
    | FieldSet (obj, f, value) -> 
        match obj with
        | Some obj -> Expr.FieldSetUnchecked (re obj, rf f, re value)
        | None -> Expr.FieldSetUnchecked (rf f, re value)
    | Let (var, value, body) -> 
        Expr.LetUnchecked(rv var, re value, re body)

    // Eliminate some F# constructs which do not cross-target well
    | Application(f,e) -> 
        re (Expr.CallUnchecked(f, f.Type.GetMethod "Invoke", [ e ]) )
    | NewUnionCase(ci, es) ->
        re (Expr.CallUnchecked(Reflection.FSharpValue.PreComputeUnionConstructorInfo ci, es) )
    | NewRecord(ci, es) ->
        re (Expr.NewObjectUnchecked(FSharpValue.PreComputeRecordConstructorInfo ci, es) )
    | UnionCaseTest(e,uc) ->
        let tagInfo = FSharpValue.PreComputeUnionTagMemberInfo uc.DeclaringType
        let tagExpr = 
            match tagInfo with 
            | :? PropertyInfo as tagProp -> Expr.PropertyGetUnchecked(e,tagProp)
            | :? MethodInfo as tagMeth -> 
                    if tagMeth.IsStatic then Expr.CallUnchecked(tagMeth, [e])
                    else Expr.CallUnchecked(e,tagMeth,[])
            | _ -> failwith "unreachable: unexpected result from PreComputeUnionTagMemberInfo"
        let tagNumber = uc.Tag
        re <@@ (%%(tagExpr) : int) = tagNumber @@>

    // Traverse remaining constructs
    | ShapeVar v -> 
        Expr.Var (rv v)
    | ShapeLambda _ -> 
        failwith ("It's not possible to create a Lambda when cross targetting to a different FSharp.Core.\n" +
                  "Make sure you're not calling a function with signature A->(B->C) instead of A->B->C (using |> causes this).")
    | ShapeCombination (o, exprs) -> 
        RebuildShapeCombination (o, List.map re exprs)

  /// Gets the equivalent runtime type
  let typeToTargetAssemblies (t:Type) = t |> replaceType true |> rttype
  /// Gets an equivalent expression with all the types replaced with runtime equivalents
  let exprToTargetAssemblies (e:Expr) = e |> replaceExpr true |> rtexpr
  /// Gets an equivalent expression with all the types replaced with designTime equivalents
  let exprToOriginalAssemblies (e:ExprRT) = e.X |> replaceExpr false

  member replacer.ProvidedParameter(paramName, typ) = 
      ProvidedParameter(paramName, typ |> typeToTargetAssemblies |> rawtype)

  member replacer.ProvidedProperty(propertyName, typ, getterCode) = 
      ProvidedProperty(propertyName, typ |> typeToTargetAssemblies |> rawtype, GetterCode = (fun args -> args |> List.map (rtexpr >> exprToOriginalAssemblies) |> getterCode |> exprToTargetAssemblies |> rawexpr))

  member replacer.ProvidedConstructor(parameters, invokeCode: Expr list -> Expr) = 
      ProvidedConstructor(parameters, InvokeCode = (fun args -> args |> List.map (rtexpr >> exprToOriginalAssemblies) |> invokeCode |> exprToTargetAssemblies |> rawexpr))

  member replacer.ProvidedMethod(nm, parameters, resultType: Type, isStatic, invokeCode: Expr list -> Expr) = 
      ProvidedMethod(nm, parameters, 
                     resultType |> typeToTargetAssemblies |> rawtype , 
                     IsStaticMethod = isStatic, 
                     InvokeCode = (fun args -> args |> List.map (rtexpr >> exprToOriginalAssemblies) |> invokeCode |> exprToTargetAssemblies |> rawexpr))

  member replacer.ProvidedTypeDefinition(nm, baseType: Type, hideObjectMethods, nonNullable) = 
      ProvidedTypeDefinition(nm, Some (baseType |> typeToTargetAssemblies |> rawtype), HideObjectMethods = hideObjectMethods, NonNullable = nonNullable)

  member replacer.ProvidedTypeDefinition(asm, ns, typeName, baseType: Type, hideObjectMethods, nonNullable) = 
      ProvidedTypeDefinition(asm, ns, typeName, Some (baseType |> typeToTargetAssemblies |> rawtype), HideObjectMethods = hideObjectMethods, NonNullable = nonNullable)


