module ApiTests

open System
open System.Threading.Tasks
open Xunit
open Vouchsafe
open FakeVouchsafe

let private answer (call: Call) =
    let path = Uri(call.Uri).AbsolutePath
    if path.EndsWith "/verifications" && call.Method = "GET" then 200, "[]"
    elif path.Contains "/verifications/" && call.Method = "GET" then 200, """{"id":"case-1"}"""
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

[<Theory>]
[<InlineData(400, "There are no known records for that postcode", true)>]
[<InlineData(400, "There are no sandbox records for that postcode. The sandbox holds addresses for SW1A 2AA, NW1 6XE, AB11 6BH", false)>]
[<InlineData(403, "There are no known records for that postcode", false)>]
[<InlineData(403, "Feature not available on your plan", false)>]
let ``only a 400 saying no known records means an unknown postcode`` (status: int, body: string, unknown: bool) =
    Assert.Equal(unknown, VouchsafeFailure.isUnknownPostcode (ApiError(status, body)))

[<Fact>]
let ``a failure that is not Vouchsafe's answer is no unknown postcode`` () =
    Assert.False(VouchsafeFailure.isUnknownPostcode (InvalidRequest "no known records"))
    Assert.False(VouchsafeFailure.isUnknownPostcode (AuthenticationRefused(400, "no known records")))

[<Theory>]
[<InlineData("Flat 1-86, 219", true)>]
[<InlineData("flat 1 - 3 231", true)>]
[<InlineData("Flat 3, 219", false)>]
[<InlineData("231-233", false)>]
[<InlineData("221B", false)>]
let ``only a range of flats is a block`` (residence: string, block: bool) =
    Assert.Equal(block, Api.SmartLookups.isFlatRange residence)

[<Fact>]
let ``reading every monitored account follows the cursor to the end`` () : Task =
    task {
        use handler =
            new FakeVouchsafeHandler(
                respondWith (fun call ->
                    if call.Uri.Contains "cursor=second" then
                        200, """{"accounts":[{}],"next_cursor":null}"""
                    else
                        200, """{"accounts":[{},{}],"next_cursor":"second"}""")
            )

        match! Api.Alerts.listEveryAccountAsync (clientWith handler) ValueNone ct with
        | Ok accounts -> Assert.Equal(3, accounts.Length)
        | Error failure -> failwith $"%A{failure}"
    }

[<Fact>]
let ``a failed page fails the whole read`` () : Task =
    task {
        use handler =
            new FakeVouchsafeHandler(
                respondWith (fun call ->
                    if call.Uri.Contains "cursor=second" then
                        503, "{}"
                    else
                        200, """{"accounts":[{}],"next_cursor":"second"}""")
            )

        match! Api.Alerts.listEveryAccountAsync (clientWith handler) ValueNone ct with
        | Ok _ -> failwith "a failed page was taken for the end"
        | Error _ -> ()
    }

type private HangingHandler() =
    inherit Net.Http.HttpMessageHandler()
    override _.SendAsync(request, ct) =
        task {
            if request.RequestUri.AbsoluteUri.EndsWith "/authenticate" then
                return new Net.Http.HttpResponseMessage(Net.HttpStatusCode.Created, Content = new Net.Http.StringContent(tokenJson "t" (DateTimeOffset.UtcNow.AddDays 1.), Text.Encoding.UTF8, "application/json"))
            else
                do! Task.Delay(Threading.Timeout.Infinite, ct)
                return null
        }

/// A success answer whose body stops with an error part way through.
type private BrokenBodyHandler() =
    inherit Net.Http.HttpMessageHandler()
    override _.SendAsync(request, _) =
        task {
            return
                if request.RequestUri.AbsoluteUri.EndsWith "/authenticate" then
                    new Net.Http.HttpResponseMessage(Net.HttpStatusCode.Created, Content = new Net.Http.StringContent(tokenJson "t" (DateTimeOffset.UtcNow.AddDays 1.), Text.Encoding.UTF8, "application/json"))
                else
                    let stream =
                        { new IO.MemoryStream() with
                            override _.ReadAsync(_: Memory<byte>, _: Threading.CancellationToken) =
                                ValueTask<int>(Task.FromException<int>(IO.IOException "connection lost")) }
                    new Net.Http.HttpResponseMessage(Net.HttpStatusCode.OK, Content = new Net.Http.StreamContent(stream))
        }

let private wrongTypeAnswer = """{"url":"https://app.vouchsafe.id/v/abc","id":12345,"flow_id":"flow-1","expires_at":"2026-11-04T12:00:00.000Z"}"""

let private verification () = VouchsafeV1.RequestVerificationInput(Email = "person@example.com")

let private expectUnreadable result =
    match result with
    | Error(InvalidResponse answer) -> answer
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an answer of the wrong type is InvalidResponse with the status and body`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 201, wrongTypeAnswer))
        let! result = Api.Verifications.requestAsync (clientWith handler) (verification ()) ct
        let answer = expectUnreadable result
        Assert.Equal(201, answer.HttpStatus)
        Assert.Equal(wrongTypeAnswer, answer.Body)
        Assert.Contains("$.id", answer.Reason)
        Assert.Single handler.ApiCalls |> ignore
    }

[<Fact>]
let ``an unreadable answer to a hand-written upload is InvalidResponse too`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 200, "<html>proxy</html>"))
        let upload = (Api.Upload.ofBytes "a.pdf" [| 1uy |]).Value
        let! result = Api.Documents.verifySupportingDocumentAsync (clientWith handler) Api.SupportingDocumentType.BankStatement upload ValueNone ct
        Assert.Equal("<html>proxy</html>", (expectUnreadable result).Body)
    }

[<Fact>]
let ``printing an unreadable answer or describing it leaves the body out`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 201, wrongTypeAnswer))
        let! result = Api.Verifications.requestAsync (clientWith handler) (verification ()) ct
        let failure = match result with Error failure -> failure | Ok _ -> failwith "read"
        for text in [ VouchsafeFailure.describe failure; $"%A{failure}"; string (expectUnreadable result) ] do
            Assert.DoesNotContain("12345", text)
            Assert.DoesNotContain("app.vouchsafe.id", text)
            Assert.Contains("201", text)
            Assert.Contains("$.id", text)
    }

[<Fact>]
let ``an unreadable answer isn't an unknown postcode`` () =
    let answer = { HttpStatus = 200; Body = "no known records"; Reason = "r" }
    Assert.False(VouchsafeFailure.isUnknownPostcode (InvalidResponse answer))

[<Fact>]
let ``a success answer cut off part way is TransportError`` () : Task =
    task {
        let client = VouchsafeClient.create credentials Endpoints.standard (new BrokenBodyHandler())
        match! Api.Verifications.requestAsync client (verification ()) ct with
        | Error(TransportError _) -> ()
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``Create without a timeout keeps the default and Create with one sets it on both versions`` () =
    let plain = VouchsafeClient.Create(credentials, Endpoints.standard)
    Assert.Equal(TimeSpan.FromSeconds 100., plain.V1.HttpClient.Timeout)
    let limited = VouchsafeClient.Create(credentials, Endpoints.standard, TimeSpan.FromSeconds 20.)
    Assert.Equal(TimeSpan.FromSeconds 20., limited.V1.HttpClient.Timeout)
    Assert.Equal(TimeSpan.FromSeconds 20., limited.V2.HttpClient.Timeout)

[<Theory; InlineData(0.); InlineData(-5.)>]
let ``Create refuses a timeout that isn't positive`` (seconds: float) =
    Assert.Throws<ArgumentOutOfRangeException>(fun () ->
        VouchsafeClient.Create(credentials, Endpoints.standard, TimeSpan.FromSeconds seconds) |> ignore)
    |> ignore

[<Fact>]
let ``a stuck call past the timeout is TransportError, not an exception`` () : Task =
    task {
        let client = VouchsafeClient(credentials, Endpoints.standard, new HangingHandler(), (fun () -> DateTimeOffset.UtcNow), ValueSome(TimeSpan.FromMilliseconds 200.))
        let call = Api.Verifications.requestAsync client (verification ()) ct
        let! winner = Task.WhenAny(call :> Task, Task.Delay 10000)
        Assert.Same(call, winner)
        match! call with
        | Error(TransportError(:? OperationCanceledException)) -> ()
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``the caller's cancellation still throws with a timeout set`` () : Task =
    task {
        use cancel = new Threading.CancellationTokenSource(TimeSpan.FromMilliseconds 100.)
        let client = VouchsafeClient(credentials, Endpoints.standard, new HangingHandler(), (fun () -> DateTimeOffset.UtcNow), ValueSome(TimeSpan.FromSeconds 30.))
        let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> Api.Verifications.requestAsync client (verification ()) cancel.Token :> Task)
        ()
    }

[<Theory>]
[<InlineData("""{"url":nul Jane Doe of 1 High Street London}""")>]
[<InlineData("""Jane Doe of 1 High Street London""")>]
[<InlineData("""{"Jane Doe of 1 High Street London":nul}""")>]
let ``a malformed answer's text stays out of describe and printing`` (body: string) : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 201, body))
        let! result = Api.Verifications.requestAsync (clientWith handler) (verification ()) ct
        let answer = expectUnreadable result
        Assert.Equal(body, answer.Body)
        let failure = InvalidResponse answer
        for text in [ VouchsafeFailure.describe failure; $"%A{failure}"; string failure; string answer; answer.Reason ] do
            Assert.DoesNotContain("Jane", text)
            Assert.DoesNotContain("High Street", text)
            Assert.DoesNotContain("London", text)
    }

[<Fact>]
let ``Reason is the JSON position of the failure`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 201, wrongTypeAnswer))
        let! result = Api.Verifications.requestAsync (clientWith handler) (verification ()) ct
        Assert.Equal("Invalid at $.id, line 0, byte 50", (expectUnreadable result).Reason)
    }

[<Fact>]
let ``a body of null is InvalidResponse, not Ok null`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 201, "null"))
        let! result = Api.Verifications.requestAsync (clientWith handler) (verification ()) ct
        let answer = expectUnreadable result
        Assert.Equal("null", answer.Body)
        Assert.Equal(201, answer.HttpStatus)
    }

[<Fact>]
let ``a body of null to a hand-written upload is InvalidResponse too`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 200, "null"))
        let upload = (Api.Upload.ofBytes "a.pdf" [| 1uy |]).Value
        let! result = Api.Documents.verifySupportingDocumentAsync (clientWith handler) Api.SupportingDocumentType.BankStatement upload ValueNone ct
        Assert.Equal("null", (expectUnreadable result).Body)
    }

[<Fact>]
let ``Create refuses a timeout HttpClient can't hold, and takes the longest it can`` () =
    let tooLong = TimeSpan.FromDays 30.
    Assert.Throws<ArgumentOutOfRangeException>(fun () -> VouchsafeClient.Create(credentials, Endpoints.standard, tooLong) |> ignore) |> ignore
    let longest = TimeSpan.FromMilliseconds(float Int32.MaxValue)
    Assert.Equal(longest, VouchsafeClient.Create(credentials, Endpoints.standard, longest).V1.HttpClient.Timeout)
    let endless = Threading.Timeout.InfiniteTimeSpan
    Assert.Equal(endless, VouchsafeClient.Create(credentials, Endpoints.standard, endless).V1.HttpClient.Timeout)

let private kept () =
    new Net.Http.HttpResponseMessage(Net.HttpStatusCode.OK, Content = new Net.Http.StringContent("{}"))

[<Fact>]
let ``transport exceptions stay TransportError even after an answer was kept`` () : Task =
    task {
        let errors: exn list =
            [ Net.Http.HttpRequestException "lost"; IO.IOException "lost"; OperationCanceledException "lost" ]
        for error in errors do
            let! result =
                Call.guarded ct (fun () ->
                    task {
                        do! LastAnswer.keep (kept ()) ct
                        return raise error
                    })
            match result with
            | Error(TransportError e) -> Assert.Same(error, e)
            | other -> failwith $"%A{(other: Result<unit, VouchsafeFailure>)}"
    }

[<Fact>]
let ``any other exception after an answer gives only its type name as Reason`` () : Task =
    task {
        let! result =
            Call.guarded ct (fun () ->
                task {
                    do! LastAnswer.keep (kept ()) ct
                    return raise (InvalidOperationException "Jane Doe")
                })
        let answer = expectUnreadable (result: Result<unit, VouchsafeFailure>)
        Assert.Equal("InvalidOperationException", answer.Reason)
    }

[<Fact>]
let ``concurrent calls report their own unreadable answers`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun call -> 200, $"<{call.Uri}>"))
        let client = clientWith handler
        let! results = [ "c1"; "c2"; "c3"; "c4" ] |> List.map (fun pid -> Api.Verifications.getAsync client pid ct) |> Task.WhenAll
        Assert.Equal<string list>(
            [ "c1>"; "c2>"; "c3>"; "c4>" ],
            results |> Array.map (fun result -> let body = (expectUnreadable result).Body in body.Substring(body.Length - 3)) |> List.ofArray)
    }

[<Theory>]
[<InlineData("{}", "The answer has no id")>]
[<InlineData("""{"id":""}""", "The answer has no id")>]
[<InlineData("""{"id":"case-1"}""", "The answer has no url")>]
let ``a started verification without its id or url is InvalidResponse`` (body: string, reason: string) : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 201, body))
        let! result = Api.Verifications.requestAsync (clientWith handler) (verification ()) ct
        let answer = expectUnreadable result
        Assert.Equal(201, answer.HttpStatus)
        Assert.Equal(body, answer.Body)
        Assert.Equal(reason, answer.Reason)
    }

[<Theory>]
[<InlineData("{}")>]
[<InlineData("""{"status":"Verified"}""")>]
let ``a verification read without its id is InvalidResponse, not one in progress`` (body: string) : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 200, body))
        let! result = Api.Verifications.getAsync (clientWith handler) "case-1" ct
        Assert.Equal("The answer has no id", (expectUnreadable result).Reason)
    }
