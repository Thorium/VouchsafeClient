# VouchsafeClient

An F# client for the [Vouchsafe](https://vouchsafe.id) identity verification API, generated with
[SwaggerProvider](https://github.com/fsprojects/SwaggerProvider) from Vouchsafe's published OpenAPI specs
(`src/VouchsafeClient/Schemas`). It targets `net10.0`.

| Module | Calls |
|---|---|
| `Api.Verifications` | request, list (by status), get, cancel |
| `Api.Documents` | supporting document, photo ID and eVisa checks without a hosted flow (beta at Vouchsafe) |
| `Api.SmartLookups` | smart lookup, postcode search |
| `Api.Risk` | v2 risk check: risk score, CLEAR / REVIEW / REJECT, credit bureau, online footprint, AML and adverse media enrichments, face scan |
| `Api.Alerts` | monitored accounts (list, get), turn monitoring on or off, acknowledge an alert |
| `Api.AdverseMedia` | adverse media check |
| `Api.Flows`, `Api.Coverage`, `Api.Artefacts`, `Api.Team` | flows, supported documents and data sources, artefact links, teams |

## Use

```fsharp
open Vouchsafe

// From the API Integration tab of the Vouchsafe dashboard; a sandbox secret puts calls in sandbox mode.
let credentials = { ClientId = "client id"; ClientSecret = "client secret" }

// One per application: it holds the token cache and the connections.
let vouchsafe = VouchsafeClient.Create(credentials, Endpoints.standard)

task {
    let input = VouchsafeV1.RequestVerificationInput(Email = "person@example.com", ExternalId = Some "customer-42")
    match! Api.Verifications.requestAsync vouchsafe input cancellationToken with
    | Ok verification -> printfn "Send the person to %s" verification.Url
    | Error failure -> printfn "%s" (VouchsafeFailure.describe failure)
}
```

Every `Api` call returns `Task<Result<_, VouchsafeFailure>>`:

- `InvalidRequest reason`: not sent. For example an id was empty, `.` or `..`, or a risk check can't be valid.
- `ApiError(status, body)`: Vouchsafe answered with a non-success status.
- `AuthenticationRefused(status, body)`: the credentials were refused.
- `TransportError exn`: there was no answer, or it could not be read.

Cancelling your token throws `OperationCanceledException` as usual. The generated clients are on the client, already
authorised: `vouchsafe.V1` and `vouchsafe.V2`. `VouchsafeClient.create credentials endpoints handler` shares a
caller-owned `HttpMessageHandler`; the client never disposes it.

## Authentication

`POST /authenticate` exchanges the client id and secret for a token valid for 24 hours. The client fetches it on first
use and keeps it until five minutes before `expires_at`; concurrent callers share one fetch. A 401 drops the token and
the request is sent once more with a new one. Printing `Credentials` or an `AccessToken` shows neither the secret nor
the token.

## Verification results

`VerificationSummary.ofVerification verification` reads a verification's status and the outcome of each step
(photo ID, video selfie, supporting document, bank account, …) and each enrichment (AML sanctions, email and phone
footprint, Radar). `VerificationSummary.stepOutcome` and `enrichmentOutcome` pick one. AML is a single check,
`AmlSanctions`, covering sanctions, watchlists and PEPs.

The detail of each step and enrichment is a union in the spec, generated as `obj`. `vouchsafe.As<'T> value` reads such a
value as the member type you name, for example
`vouchsafe.As<VouchsafeV1.PassportExtractedDetails> response.ExtractedDetails`.

## Uploads

`Api.Documents.verifySupportingDocumentAsync`, `verifyPhotoIdAsync` and `Api.Risk.checkAsync` send multipart forms
built here rather than by the generated client: each file goes with its own name and media type (`Upload.ofBytes`
types PDF, JPEG and PNG by extension), and text fields as plain text. `PhotoId` carries what each kind needs: the back
image for a national ID, the issuing country for a driving licence.

`RiskCheck.create` starts a risk check from the four required fields. The checks that can be made before sending are:
- the credit bureau enrichment needs an address;
- an address needs a residence number or a building name, a city, a postcode and a country code;
- thresholds are 0-100.

A check that fails them is `InvalidRequest` and isn't sent.

## Webhooks

Vouchsafe deliveries are not signed. `Webhook.parse body` reads one:
- `Verification` events: `Created`, `UserCompleted`, `Verified`, `Refused`, `ManuallyReviewed`, `Expired`;
- `Alert` events, with the case or smart lookup they concern.

Put a long unguessable token in the webhook URL and check it with `Webhook.tokenMatches` (constant time). Act on
`Webhook.confirmAsync`, which fetches the verification as Vouchsafe holds it, rather than on the delivery's own fields.

## Changes to the bundled specs

The specs are Vouchsafe's downloads with one edit: two references whose target name ends with the referring property's
name (`Environment` → `_36_Enums.Environment`, `items` → `CoverageItem`) are wrapped as a one-element `allOf`. Plain
references would hit a SwaggerProvider name clash ("Cannot compile object … based on unresolved reference"). The
wrapped type is the same.

## Tests

`dotnet test` runs the tests against a fake handler.
