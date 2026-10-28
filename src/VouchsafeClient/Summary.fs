namespace Vouchsafe

open System.Text.Json

/// The outcome of a verification step or enrichment.
[<RequireQualifiedAccess>]
type Outcome =
    | Pass
    | Fail
    | Inconclusive
    | InProgress
    | Error
    | OtherOutcome of code: string

/// A step of a verification flow.
[<RequireQualifiedAccess>]
type Step =
    | VideoSelfie
    | VerifyEmail
    | VerifyPhoneNumber
    | VerifyUser
    | PhotoId
    | SupportingDocument
    | BankAccount
    | Evisa
    /// A referee's vouch.
    | Vouch
    | Identity
    | Address
    | OtherStep of code: string

/// An enrichment run on a verification. AML is one check covering sanctions, watchlists and PEPs.
[<RequireQualifiedAccess>]
type EnrichmentCheck =
    | AmlSanctions
    | EmailFootprint
    | PhoneFootprint
    | Radar
    | OtherEnrichment of code: string

type StepResult = { Step: Step; Outcome: Outcome voption }

type EnrichmentResult = { Check: EnrichmentCheck; Outcome: Outcome voption }

/// A verification's status and the outcome of each step and enrichment; the detail of each stays on the generated types.
type VerificationSummary =
    { Status: VouchsafeV1.CaseStatus
      Steps: StepResult list
      Enrichments: EnrichmentResult list }

module VerificationSummary =

    let private outcome code =
        match code with
        | "pass" -> Outcome.Pass
        | "fail" -> Outcome.Fail
        | "inconclusive" -> Outcome.Inconclusive
        | "in_progress" -> Outcome.InProgress
        | "error" -> Outcome.Error
        | other -> Outcome.OtherOutcome other

    let private step code =
        match code with
        | "video_selfie" -> Step.VideoSelfie
        | "verify_email" -> Step.VerifyEmail
        | "verify_phone_number" -> Step.VerifyPhoneNumber
        | "verify_user" -> Step.VerifyUser
        | "photo_id" -> Step.PhotoId
        | "supporting_document" -> Step.SupportingDocument
        | "bank_account" -> Step.BankAccount
        | "evisa" -> Step.Evisa
        | "vouch" -> Step.Vouch
        | "step_identity" -> Step.Identity
        | "step_address" -> Step.Address
        | other -> Step.OtherStep other

    let private enrichment code =
        match code with
        | "aml_sanctions" -> EnrichmentCheck.AmlSanctions
        | "email_footprint_analysis" -> EnrichmentCheck.EmailFootprint
        | "phone_footprint_analysis" -> EnrichmentCheck.PhoneFootprint
        | "radar" -> EnrichmentCheck.Radar
        | other -> EnrichmentCheck.OtherEnrichment other

    let private text (item: obj) (name: string) =
        match item with
        | :? JsonElement as element when element.ValueKind = JsonValueKind.Object ->
            match element.TryGetProperty name with
            | true, value when value.ValueKind = JsonValueKind.String -> ValueSome(value.GetString())
            | _ -> ValueNone
        | _ -> ValueNone

    let private read (items: obj[]) (key: string) (make: string -> Outcome voption -> 'T) =
        items
        |> Option.ofObj
        |> Option.defaultValue [||]
        |> Seq.choose (fun item ->
            match text item key with
            | ValueSome code -> Some(make code (text item "outcome" |> ValueOption.map outcome))
            | ValueNone -> None)
        |> List.ofSeq

    let ofVerification (verification: VouchsafeV1.GetVerificationResponse) =
        { Status = verification.Status
          Steps = read verification.Checks "step" (fun code result -> { Step = step code; Outcome = result })
          Enrichments = read verification.Enrichments "check" (fun code result -> { Check = enrichment code; Outcome = result }) }

    /// The outcome of the first instance of a step; ValueNone when the flow has no such step or it has no outcome yet.
    let stepOutcome (wanted: Step) (summary: VerificationSummary) =
        summary.Steps |> List.tryFind (fun s -> s.Step = wanted) |> Option.toValueOption |> ValueOption.bind _.Outcome

    /// The outcome of an enrichment; ValueNone when it was not run.
    let enrichmentOutcome (wanted: EnrichmentCheck) (summary: VerificationSummary) =
        summary.Enrichments |> List.tryFind (fun e -> e.Check = wanted) |> Option.toValueOption |> ValueOption.bind _.Outcome
