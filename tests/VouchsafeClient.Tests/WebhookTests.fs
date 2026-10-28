module WebhookTests

open System.Threading.Tasks
open Xunit
open Vouchsafe
open FakeVouchsafe

[<Fact>]
let ``verification deliveries read as typed events`` () =
    let body =
        """{"event":"Verified","id":"case-1","email":"person@example.com","redirect_url":null,"status":"Verified","created_at":"2026-10-28T10:00:00.000Z","expires_at":"2026-11-04T10:00:00.000Z","flow_id":"flow-1","workflow_id":null,"external_id":"holdwell-42"}"""
    match Webhook.parse body with
    | Ok(Webhook.Verification event) ->
        Assert.Equal(Webhook.Verified, event.Kind)
        Assert.Equal("case-1", event.VerificationId)
        Assert.Equal(ValueSome "holdwell-42", event.ExternalId)
        Assert.Equal(ValueSome "flow-1", event.FlowId)
        Assert.Equal(ValueNone, event.WorkflowId)
    | other -> failwith $"%A{other}"
    for name, kind in [ "Created", Webhook.Created; "UserCompleted", Webhook.UserCompleted; "Refused", Webhook.Refused; "ManuallyReviewed", Webhook.ManuallyReviewed; "Expired", Webhook.Expired; "Paused", Webhook.OtherEvent "Paused" ] do
        match Webhook.parse $"""{{"event":"{name}","id":"case-1"}}""" with
        | Ok(Webhook.Verification event) -> Assert.Equal(kind, event.Kind)
        | other -> failwith $"%A{other}"

[<Fact>]
let ``alert deliveries read with their case or smart lookup`` () =
    match Webhook.parse """{"event":"Alert","alertId":"alert-1","smartLookupId":"lookup-1"}""" with
    | Ok(Webhook.Alert alert) ->
        Assert.Equal("alert-1", alert.AlertId)
        Assert.Equal(ValueNone, alert.CaseId)
        Assert.Equal(ValueSome "lookup-1", alert.SmartLookupId)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``malformed deliveries are errors`` () =
    for body in [ "not json"; "[]"; """{"id":"case-1"}"""; """{"event":"Verified"}"""; """{"event":"Alert"}""" ] do
        Assert.True(Result.isError (Webhook.parse body), body)

[<Fact>]
let ``the URL token is compared exactly`` () =
    Assert.True(Webhook.tokenMatches "Zr8pL2xQ9vT4" "Zr8pL2xQ9vT4")
    Assert.False(Webhook.tokenMatches "Zr8pL2xQ9vT4" "Zr8pL2xQ9vT5")
    Assert.False(Webhook.tokenMatches "Zr8pL2xQ9vT4" "Zr8pL2xQ9vT")
    Assert.False(Webhook.tokenMatches "Zr8pL2xQ9vT4" null)
    Assert.False(Webhook.tokenMatches "" "")

[<Fact>]
let ``confirmAsync fetches the verification the delivery names`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 200, """{"id":"case-1","status":"Verified","created_at":"2026-10-28T10:00:00.000Z","expires_at":"2026-11-04T10:00:00.000Z"}"""))
        let event =
            match Webhook.parse """{"event":"Verified","id":"case-1"}""" with
            | Ok(Webhook.Verification event) -> event
            | other -> failwith $"%A{other}"
        let! result = Webhook.confirmAsync (clientWith handler) event ct
        Assert.Equal(VouchsafeV1.CaseStatus.Verified, (expectOk result).Status)
        Assert.Equal("https://app.vouchsafe.id/api/v1/verifications/case-1", (Assert.Single handler.ApiCalls).Uri)
    }

[<Fact>]
let ``a verification summary reads each step's and enrichment's outcome`` () =
    use handler = new FakeVouchsafeHandler(fun _ -> 200, "{}")
    let client = clientWith handler
    let json =
        """{"id":"case-1","status":"ReadyForReview","created_at":"2026-10-28T10:00:00.000Z","expires_at":"2026-11-04T10:00:00.000Z",
            "checks":[{"step":"photo_id","outcome":"pass"},{"step":"video_selfie","outcome":"fail"},{"step":"verify_email"},{"step":"new_step","outcome":"maybe"}],
            "enrichments":[{"check":"aml_sanctions","outcome":"fail","details":{}},{"check":"radar","outcome":"pass"}]}"""
    let verification = client.V1.Deserialize(json, typeof<VouchsafeV1.GetVerificationResponse>) :?> VouchsafeV1.GetVerificationResponse
    let summary = VerificationSummary.ofVerification verification
    Assert.Equal(VouchsafeV1.CaseStatus.ReadyForReview, summary.Status)
    Assert.Equal(ValueSome Outcome.Pass, VerificationSummary.stepOutcome Step.PhotoId summary)
    Assert.Equal(ValueSome Outcome.Fail, VerificationSummary.stepOutcome Step.VideoSelfie summary)
    Assert.Equal(ValueNone, VerificationSummary.stepOutcome Step.VerifyEmail summary)
    Assert.Equal(ValueNone, VerificationSummary.stepOutcome Step.BankAccount summary)
    Assert.Contains({ Step = Step.OtherStep "new_step"; Outcome = ValueSome(Outcome.OtherOutcome "maybe") }, summary.Steps)
    Assert.Equal(ValueSome Outcome.Fail, VerificationSummary.enrichmentOutcome EnrichmentCheck.AmlSanctions summary)
    Assert.Equal(ValueSome Outcome.Pass, VerificationSummary.enrichmentOutcome EnrichmentCheck.Radar summary)
    Assert.Equal(ValueNone, VerificationSummary.enrichmentOutcome EnrichmentCheck.EmailFootprint summary)
