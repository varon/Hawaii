open System
open Microsoft.OpenApi.Readers
open System.Net.Http
open FsAst
open Fantomas
open FSharp.Compiler.SyntaxTree
open FSharp.Compiler.Range
open FSharp.Compiler.XmlDoc
open Microsoft.OpenApi.Models
open System.Linq
open Microsoft.OpenApi.Interfaces

let schema = "https://petstore.swagger.io/v2/swagger.json"
let project = "PetStore"
let client = new HttpClient()

let capitalize (input: string) =
    if String.IsNullOrWhiteSpace input
    then ""
    else input.First().ToString().ToUpper() + String.Join("", input.Skip(1))

let rec createFSharpType recordName required (propertyName: string) (propertySchema: OpenApiSchema) =
    if not required then
        let optionalType : SynType = createFSharpType recordName true propertyName propertySchema
        SynType.Option(optionalType)
    else
        match propertySchema.Type with
        | "integer" when propertySchema.Format = "int64" -> SynType.Int64()
        | "integer" when propertySchema.Format = "int32" -> SynType.Int()
        | "boolean" -> SynType.Bool()
        | "string" when propertySchema.Format = "uuid" -> SynType.CreateLongIdent(LongIdentWithDots.Create [ "System"; "Guid" ])
        | "string" when propertySchema.Format = "date-time" -> SynType.DateTimeOffset()
        | "array" ->
            let arrayItemsType = createFSharpType recordName required propertyName propertySchema.Items
            SynType.List(arrayItemsType)
        | "string" when not (isNull propertySchema.Enum) && propertySchema.Enum.Count > 0 ->
            SynType.Create(recordName + capitalize propertyName)
        | _ ->
            SynType.String()

let compiledName (name: string) = SynAttribute.Create("CompiledName", name)

let createEnumType (enumType: (string * seq<string>)) =
    let info : SynComponentInfoRcd = {
        Access = None
        Attributes = [
            SynAttributeList.Create [
                SynAttribute.RequireQualifiedAccess()
            ]
        ]

        Id = [ Ident.Create (fst enumType) ]
        XmlDoc = PreXmlDoc.Empty
        Parameters = [ ]
        Constraints = [ ]
        PreferPostfix = false
        Range = range0
    }

    let values = snd enumType

    let enumRepresentation = SynTypeDefnSimpleReprUnionRcd.Create([
        for value in values ->
            let attrs = [ SynAttributeList.Create [| compiledName value  |] ]
            let docs = PreXmlDoc.Empty
            SynUnionCase.UnionCase(attrs, Ident.Create (capitalize value), SynUnionCaseType.UnionCaseFields [], docs, None, range0)
    ])

    let simpleType = SynTypeDefnSimpleReprRcd.Union(enumRepresentation)
    SynModuleDecl.CreateSimpleType(info, simpleType)

[<EntryPoint>]
let main argv =
    let response =
        client.GetStreamAsync(schema)
        |> Async.AwaitTask
        |> Async.RunSynchronously

    let reader = new OpenApiStreamReader()
    let (openApiDocument, diagnostics) =  reader.Read(response)

    let createRecordFromSchema (recordName: string) (schema: OpenApiSchema) =
        let info : SynComponentInfoRcd = {
            Access = None
            Attributes = [ ]
            Id = [ Ident.Create recordName ]
            XmlDoc = PreXmlDoc.Create [ if String.isNotNullOrEmpty schema.Description then schema.Description ]
            Parameters = [ ]
            Constraints = [ ]
            PreferPostfix = false
            Range = range0
        }

        let recordRepresentation =  SynTypeDefnSimpleReprRecordRcd.Create [
            for property in schema.Properties do
                // todo: infer the types correctly
                let propertyName = property.Key
                let propertyType = property.Value
                let required = schema.Required.Contains propertyName
                let field = SynFieldRcd.Create(propertyName, createFSharpType recordName required propertyName propertyType)
                let docs = PreXmlDoc.Create [ if String.isNotNullOrEmpty propertyType.Description then propertyType.Description ]
                { field with XmlDoc = docs }
        ]

        let simpleType = SynTypeDefnSimpleReprRcd.Record recordRepresentation
        SynModuleDecl.CreateSimpleType(info, simpleType)

    // give me a (enumName * enumCase list) list
    let rec findEnumTypes (parentName: string) (enumName: string option) (schema: OpenApiSchema) =
        // when schema is an actual enum
        if not (isNull schema.Enum) && schema.Enum.Count > 0 then
            match enumName with
            | Some name ->
                let cases =
                    schema.Enum
                    |> Seq.choose (fun enumCase ->
                        match enumCase with
                        | :? Microsoft.OpenApi.Any.OpenApiString as primitiveValue -> Some primitiveValue.Value
                        | _ -> None)

                [ (name, cases) ]
            | None ->
                [ ]
        else
            [
                for property in schema.Properties do
                    let propertyName = property.Key
                    let propertySchema = property.Value
                    yield! findEnumTypes parentName (Some (parentName + capitalize propertyName)) propertySchema
            ]

    let enumDefinitions = [
        for schema in openApiDocument.Components.Schemas do
            let typeName = schema.Key
            for (enumName, enumCases) in findEnumTypes typeName None schema.Value do
                if not (Seq.isEmpty enumCases) then
                    enumName, enumCases
    ]

    let enumTypes =
        enumDefinitions
        |> List.map createEnumType

    let globalTypes = [
        yield! enumTypes
        for schema in openApiDocument.Components.Schemas do
            if schema.Value.Type = "object"
            then createRecordFromSchema schema.Key schema.Value
    ]

    let globalTypesModule = CodeGen.createNamespace [ project; "Types" ] globalTypes

    let code = CodeGen.formatAst (CodeGen.createFile [ globalTypesModule ])

    System.Console.WriteLine code
    0 // return an integer exit code