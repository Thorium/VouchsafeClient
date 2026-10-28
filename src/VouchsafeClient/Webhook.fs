/// Vouchsafe webhook deliveries. They are not signed: put a long unguessable token in the webhook URL and check it with
/// tokenMatches, or confirm the delivery with confirmAsync before acting on it.
module Vouchsafe.Webhook

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading

type VerificationEventKind =
    /// The verification was requested, from the dashboard or an integration.
    | Created
    /// The person finished their steps; further action is needed, such as a referee's input.
    | UserCompleted
    /// Verified automatically.
    | Verified
    /// Refused on fraud signals that need your review.
    | Refused
    /// A team member approved a case sent for manual review.
    | ManuallyReviewed
    /// The person did not finish before the expiry date.
    | Expired
    | OtherEvent of name: string

type VerificationEvent =
    { Kind: VerificationEventKind
      VerificationId: string
      Status: string voption
      Email: string voption
      ExternalId: string voption
      FlowId: string voption
      WorkflowId: string voption
      CreatedAt: string voption
      ExpiresAt: string voption }

/// A due-diligence alert, such as a sanctions match: on a verification (CaseId) or on a smart lookup or risk check.
type AlertEvent =
    { AlertId: string
      CaseId: string voption
      SmartLookupId: string voption }

type WebhookEvent =
    | Verification of VerificationEvent
    | Alert of AlertEvent

let private text (root: JsonElement) (name: string) =
    match root.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetString())) -> ValueSome(value.GetString())
    | _ -> ValueNone

let private kind name =
    match name with
    | "Created" -> Created
    | "UserCompleted" -> UserCompleted
    | "Verified" -> Verified
    | "Refused" -> Refused
    | "ManuallyReviewed" -> ManuallyReviewed
    | "Expired" -> Expired
    | other -> OtherEvent other

/// The event in a delivery body, or why it is not one.
let parse (body: string) : Result<WebhookEvent, string> =
    try
        use document = JsonDocument.Parse body
        let root = document.RootElement
        if root.ValueKind <> JsonValueKind.Object then
            Error "the body is not a JSON object"
        else
            match text root "event" with
            | ValueNone -> Error "the body has no event"
            | ValueSome "Alert" ->
                match text root "alertId" with
                | ValueSome alertId ->
                    Ok(Alert { AlertId = alertId; CaseId = text root "caseId"; SmartLookupId = text root "smartLookupId" })
                | ValueNone -> Error "the Alert event has no alertId"
            | ValueSome name ->
                match text root "id" with
                | ValueSome pid ->
                    Ok(
                        Verification
                            { Kind = kind name
                              VerificationId = pid
                              Status = text root "status"
                              Email = text root "email"
                              ExternalId = text root "external_id"
                              FlowId = text root "flow_id"
                              WorkflowId = text root "workflow_id"
                              CreatedAt = text root "created_at"
                              ExpiresAt = text root "expires_at" }
                    )
                | ValueNone -> Error $"the {name} event has no id"
    with :? JsonException as e -> Error $"the body is not JSON: {e.Message}"

/// True when the token received (from the webhook URL) equals the expected one; compared in constant time.
let tokenMatches (expected: string) (received: string) =
    not (String.IsNullOrEmpty expected || isNull received)
    && CryptographicOperations.FixedTimeEquals(ReadOnlySpan(Encoding.UTF8.GetBytes expected), ReadOnlySpan(Encoding.UTF8.GetBytes received))

/// The verification as Vouchsafe holds it now. Act on this rather than on the delivery's own fields.
let confirmAsync (client: VouchsafeClient) (event: VerificationEvent) (ct: CancellationToken) =
    Api.Verifications.getAsync client event.VerificationId ct
