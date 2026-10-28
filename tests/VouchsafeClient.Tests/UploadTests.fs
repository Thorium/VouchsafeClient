module UploadTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Vouchsafe
open Vouchsafe.Api
open FakeVouchsafe

let private pdf = (Upload.ofBytes "statement.pdf" "%PDF-1.4 fictitious"B).Value
let private jpeg name = (Upload.ofBytes name [| 0xFFuy; 0xD8uy; 0xFFuy |]).Value

/// The Content-Disposition and Content-Type lines of each part, in order.
let private parts (body: string) =
    body.Split([| "\r\n" |], StringSplitOptions.None)
    |> Array.filter (fun line -> line.StartsWith "Content-Disposition" || line.StartsWith "Content-Type")
    |> List.ofArray

[<Fact>]
let ``uploads are typed by extension; other extensions are refused`` () =
    Assert.Equal("application/pdf", pdf.ContentType)
    Assert.Equal("image/jpeg", (jpeg "front.JPEG").ContentType)
    Assert.Equal("image/png", (Upload.ofBytes "scan.png" [||]).Value.ContentType)
    Assert.Equal(ValueNone, Upload.ofBytes "statement.docx" [||])

[<Fact>]
let ``a supporting document goes as a named file with its type, and plain text fields`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 200, """{"verification_method":"supporting_document","evidence_type":"bank_statement","billable":true,"outcome":"pass"}"""))
        let! result = Documents.verifySupportingDocumentAsync (clientWith handler) SupportingDocumentType.BankStatement pdf (ValueSome 3) ct
        let response = expectOk result
        Assert.Equal(VouchsafeV1.SupportingDocumentEvidenceType.BankStatement, response.EvidenceType)
        let call = Assert.Single handler.ApiCalls
        Assert.Equal("POST https://app.vouchsafe.id/api/v1/verify/supporting-documents", $"{call.Method} {call.Uri}")
        Assert.StartsWith("multipart/form-data", call.Headers["Content-Type"])
        Assert.Equal<string list>(
            [ "Content-Disposition: form-data; name=sub_type"
              "Content-Disposition: form-data; name=minimum_document_length"
              "Content-Type: application/pdf"
              "Content-Disposition: form-data; name=document; filename=statement.pdf; filename*=utf-8''statement.pdf" ],
            parts call.Body)
        Assert.Contains("\r\n\r\nBankStatement\r\n", call.Body)
        Assert.Contains("\r\n\r\n3\r\n", call.Body)
        Assert.Contains("%PDF-1.4 fictitious", call.Body)
    }

[<Fact>]
let ``photo ID kinds send what each needs`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 200, "{}"))
        let client = clientWith handler
        let! _ = Documents.verifyPhotoIdAsync client (PhotoId.NationalId(jpeg "back.jpg")) (jpeg "front.jpg") (ValueSome(jpeg "face.jpg")) ct
        let! _ = Documents.verifyPhotoIdAsync client (PhotoId.DrivingLicence "GB") (jpeg "front.jpg") ValueNone ct
        let! _ = Documents.verifyPhotoIdAsync client PhotoId.PassCard (jpeg "front.jpg") ValueNone ct
        let names (call: Call) =
            parts call.Body
            |> List.filter (fun line -> line.StartsWith "Content-Disposition")
            |> List.map (fun line -> (line.Split ";").[1].Trim())
        let calls = handler.ApiCalls
        Assert.Equal<string list>([ "name=sub_type"; "name=front"; "name=back"; "name=face_scan" ], names calls[0])
        Assert.Contains("\r\n\r\nNationalId\r\n", calls[0].Body)
        Assert.Equal<string list>([ "name=sub_type"; "name=country_code"; "name=front" ], names calls[1])
        Assert.Contains("\r\n\r\nGB\r\n", calls[1].Body)
        Assert.Contains("\r\n\r\nPASSCard\r\n", calls[2].Body)
        Assert.All(calls, fun call -> Assert.Equal("https://app.vouchsafe.id/api/v1/verify/photo-id", call.Uri))
    }

let private person = RiskCheck.create "Testy" "Fictitious" "person@example.com" (DateOnly(1990, 5, 17))

let private downing =
    { ResidenceNumber = ValueSome "10"
      BuildingName = ValueNone
      Street = ValueSome "Downing Street"
      City = "London"
      Postcode = "SW1A 2AA"
      Region = ValueNone
      CountryCode = "GB" }

[<Fact>]
let ``a risk check sends its fields with JSON-encoded enrichments, address and thresholds`` () =
    let check =
        { person with
            Phone = ValueSome "+447700900123"
            Enrichments = [ Enrichment.CreditBureau; Enrichment.Aml; Enrichment.Aml ]
            Address = ValueSome downing
            AmlThreshold = ValueSome 90 }
    let fields = (RiskCheck.fields check |> Result.defaultWith (fun e -> failwith $"%A{e}")) |> Map.ofList
    Assert.Equal("Testy", fields["first_name"])
    Assert.Equal("1990-05-17", fields["date_of_birth"])
    Assert.Equal("+447700900123", fields["phone"])
    Assert.Equal("""["CREDIT_BUREAU","AML"]""", fields["enrichments"])
    let address = JsonDocument.Parse(fields["address"]).RootElement
    Assert.Equal("10", address.GetProperty("residence_number").GetString())
    Assert.Equal(JsonValueKind.Null, address.GetProperty("building_name").ValueKind)
    Assert.Equal("SW1A 2AA", address.GetProperty("postcode").GetString())
    Assert.Equal("""{"aml":90}""", fields["thresholds"])

[<Fact>]
let ``a risk check that cannot be valid is InvalidRequest`` () =
    let reason check =
        match RiskCheck.fields check with
        | Error(InvalidRequest reason) -> reason
        | other -> failwith $"%A{other}"
    Assert.Contains("needs an address", reason { person with Enrichments = [ Enrichment.CreditBureau ] })
    Assert.Contains("residence number or a building name", reason { person with Address = ValueSome { downing with ResidenceNumber = ValueNone } })
    Assert.Contains("0-100", reason { person with OnlineFootprintThreshold = ValueSome 101 })
    Assert.Contains("required", reason { person with Email = " " })

[<Fact>]
let ``a risk check posts to v2 with the face scan as a file`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 201, """{"risk_score":12,"recommendation":"CLEAR"}"""))
        let! result = Risk.checkAsync (clientWith handler) { person with FaceScan = ValueSome(jpeg "selfie.jpg") } ct
        expectOk result |> ignore
        let call = Assert.Single handler.ApiCalls
        Assert.Equal("POST https://app.vouchsafe.id/api/v2/risk", $"{call.Method} {call.Uri}")
        Assert.Contains("name=face_scan; filename=selfie.jpg", call.Body)
        Assert.DoesNotContain("name=enrichments", call.Body)
    }

[<Fact>]
let ``an invalid risk check is not sent`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 201, "{}"))
        let! result = Risk.checkAsync (clientWith handler) { person with Enrichments = [ Enrichment.CreditBureau ] } ct
        Assert.True(match result with Error(InvalidRequest _) -> true | _ -> false)
        Assert.Empty handler.Calls
    }

[<Fact>]
let ``an upload refused with 401 is sent again in full with a fresh token`` () : Task =
    task {
        let api (call: Call) = if call.Headers["Authorization"] = "Bearer tok-1" then 401, "{}" else 200, """{"verification_method":"supporting_document","evidence_type":"payslip","billable":true,"outcome":"pass"}"""
        use handler = new FakeVouchsafeHandler(respondWith api)
        let! result = Documents.verifySupportingDocumentAsync (clientWith handler) SupportingDocumentType.Payslip pdf ValueNone ct
        expectOk result |> ignore
        let first, second = handler.ApiCalls[0], handler.ApiCalls[1]
        Assert.Equal("Bearer tok-2", second.Headers["Authorization"])
        Assert.Contains("%PDF-1.4 fictitious", second.Body)
        Assert.Equal(first.Body.Length, second.Body.Length)
    }
