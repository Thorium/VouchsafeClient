module ApiTests

open System
open System.Threading.Tasks
open Xunit
open Vouchsafe
open FakeVouchsafe

let private answer (call: Call) =
    let path = Uri(call.Uri).AbsolutePath
    if path.EndsWith "/verifications" && call.Method = "GET" then 200, "[]"
    elif path.EndsWith "/flows" || path.EndsWith "/coverage" then 200, "[]"
    else 200, "{}"

[<Fact>]
let ``each call goes to its path, with ids escaped and enum filters as Vouchsafe spells them`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith answer)
        let client = clientWith handler
        let! r1 = Api.Verifications.listAsync client (ValueSome VouchsafeV1.CaseStatus.ReadyForReview) ct
        let! r2 = Api.Verifications.getAsync client "case 1/../x" ct
        let! r3 = Api.Verifications.cancelAsync client "case-2" ct
        let! r4 = Api.Coverage.listAsync client (ValueSome "GB") (ValueSome VouchsafeV1.CoverageType.DigitalId) ct
        let! r5 = Api.Alerts.listAccountsAsync client (ValueSome VouchsafeV1.AlertAccountStatus.New) (ValueSome "next") ct
        let! r6 = Api.Alerts.acknowledgeAsync client "alert?x" ct
        let! r7 = Api.Flows.getAsync client "flow#1" ct
        let! r8 = Api.Artefacts.getAsync client "front.jpg" ct
        for result in [ box r1; box r2; box r3; box r4; box r5; box r6; box r7; box r8 ] do
            Assert.Contains("Ok", $"%A{result}")
        List.iter2
            (fun (expected: string) (call: Call) -> Assert.Equal(expected, $"{call.Method} {call.Uri}"))
            [ "GET https://app.vouchsafe.id/api/v1/verifications?status=ReadyForReview"
              "GET https://app.vouchsafe.id/api/v1/verifications/case%201%2F..%2Fx"
              "POST https://app.vouchsafe.id/api/v1/verifications/case-2/cancel"
              "GET https://app.vouchsafe.id/api/v1/coverage?country_code=GB&type=DIGITAL_ID"
              "GET https://app.vouchsafe.id/api/v1/alerts/accounts?status=new&cursor=next"
              "PATCH https://app.vouchsafe.id/api/v1/alerts/alert%3Fx"
              "GET https://app.vouchsafe.id/api/v1/flows/flow%231"
              "GET https://app.vouchsafe.id/api/v1/artefacts/front.jpg" ]
            handler.ApiCalls
    }

[<Fact>]
let ``an empty or dot id is InvalidRequest and nothing is sent`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith answer)
        let client = clientWith handler
        for value in [ ""; "."; ".."; null ] do
            match! Api.Verifications.getAsync client value ct with
            | Error(InvalidRequest reason) -> Assert.Contains("verificationId", reason)
            | other -> failwith $"%A{other}"
        Assert.Empty handler.Calls
    }

[<Fact>]
let ``a verification request leaves unset fields out`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 201, """{"url":"https://app.vouchsafe.id/v/abc","id":"case-1","flow_id":"flow-1","expires_at":"2026-11-04T12:00:00.000Z"}"""))
        let input = VouchsafeV1.RequestVerificationInput(Email = "person@example.com", FirstName = Some "Testy", ExternalId = Some "holdwell-42")
        let! result = Api.Verifications.requestAsync (clientWith handler) input ct
        let response = expectOk result
        Assert.Equal("case-1", response.Id)
        let call = Assert.Single handler.ApiCalls
        Assert.Equal("POST https://app.vouchsafe.id/api/v1/verifications", $"{call.Method} {call.Uri}")
        Assert.DoesNotContain("null", call.Body)
        Assert.Contains("\"external_id\":\"holdwell-42\"", call.Body)
    }

[<Fact>]
let ``a refused request is ApiError with the status and body`` () : Task =
    task {
        let body = """{"statusCode":400,"message":"Invalid country"}"""
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 400, body))
        match! Api.Coverage.listAsync (clientWith handler) (ValueSome "XX") ValueNone ct with
        | Error(ApiError(400, text)) -> Assert.Equal(body, text)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``a union-typed field reads as the member type asked for`` () =
    use handler = new FakeVouchsafeHandler(fun _ -> 200, "{}")
    let client = clientWith handler
    let json = """{"verification_method":"supporting_document","evidence_type":"bank_statement","billable":true,"outcome":"pass","extracted_details":{"first_name":"Testy","last_name":"Fictitious"},"validations":{}}"""
    let response = client.V1.Deserialize(json, typeof<VouchsafeV1.SupportingDocumentVerificationResponse>) :?> VouchsafeV1.SupportingDocumentVerificationResponse
    let details = client.As<VouchsafeV1.SupportingDocumentExtractedDetails> response.ExtractedDetails
    Assert.True details.IsSome
    Assert.Equal(ValueNone, client.As<VouchsafeV1.SupportingDocumentExtractedDetails> null)

[<Fact>]
let ``endpoints without a trailing slash still reach the API paths`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 200, "{}"))
        let client = VouchsafeClient.create credentials { V1 = Uri "https://app.vouchsafe.id/api/v1"; V2 = Uri "https://app.vouchsafe.id/api/v2" } handler
        let! result = Api.Team.getAsync client ct
        expectOk result |> ignore
        Assert.Equal<string list>([ "https://app.vouchsafe.id/api/v1/authenticate"; "https://app.vouchsafe.id/api/v1/team" ], handler.Calls |> List.map _.Uri)
    }
