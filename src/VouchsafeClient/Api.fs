/// Task-based calls. Each returns Ok with the generated response type, or Error VouchsafeFailure.
/// String path parameters are escaped as one segment, so they cannot reach another endpoint.
module Vouchsafe.Api

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Vouchsafe.Call

/// A file to upload: its name with extension, its media type and its bytes.
type Upload =
    { FileName: string
      ContentType: string
      Content: byte[] }

module Upload =

    /// The media type for a PDF, JPEG or PNG file name; ValueNone for any other extension.
    let contentTypeOf (fileName: string) =
        match Path.GetExtension(fileName).ToLowerInvariant() with
        | ".pdf" -> ValueSome "application/pdf"
        | ".jpg"
        | ".jpeg" -> ValueSome "image/jpeg"
        | ".png" -> ValueSome "image/png"
        | _ -> ValueNone

    /// An upload of the bytes under the file name, typed by its extension (PDF, JPEG or PNG).
    let ofBytes (fileName: string) (content: byte[]) =
        contentTypeOf fileName
        |> ValueOption.map (fun contentType -> { FileName = fileName; ContentType = contentType; Content = content })

/// Sends a multipart form to a generated client's base address and reads the answer as 'T. Files carry their own name
/// and media type; text fields carry no media type, as browsers send them.
let private postFormAsync<'T>
    (generated: Swagger.ProvidedApiClientBase)
    (path: string)
    (fields: (string * string) list)
    (files: (string * Upload) list)
    (ct: CancellationToken)
    : Task<Result<'T, VouchsafeFailure>> =
    guarded ct (fun () -> task {
        use content = new MultipartFormDataContent()
        for name, value in fields do
            let part = new StringContent(value)
            part.Headers.ContentType <- null
            content.Add(part, name)
        for name, file in files do
            let part = new ByteArrayContent(file.Content)
            part.Headers.ContentType <- MediaTypeHeaderValue file.ContentType
            content.Add(part, name, file.FileName)
        use message = new HttpRequestMessage(HttpMethod.Post, path, Content = content)
        message.Headers.Accept.ParseAdd "application/json"
        use! response = generated.HttpClient.SendAsync(message, ct)
        let! text = response.Content.ReadAsStringAsync ct
        return
            if response.IsSuccessStatusCode then
                Ok(generated.Deserialize(text, typeof<'T>) :?> 'T)
            else
                Error(ApiError(int response.StatusCode, text)) })

/// Verification requests: the hosted flow a person completes.
module Verifications =

    let requestAsync (client: VouchsafeClient) (input: VouchsafeV1.RequestVerificationInput) (ct: CancellationToken) =
        attempt ct (client.V1.RequestVerification(input, ct))

    /// Verifications, optionally only those with the status.
    let listAsync (client: VouchsafeClient) (status: VouchsafeV1.CaseStatus voption) (ct: CancellationToken) =
        attempt ct (client.V1.ListVerifications(ValueOption.toOption status, ct))

    let getAsync (client: VouchsafeClient) (verificationId: string) (ct: CancellationToken) =
        withSegment "verificationId" verificationId (fun escaped -> attempt ct (client.V1.GetVerification(escaped, ct)))

    let cancelAsync (client: VouchsafeClient) (verificationId: string) (ct: CancellationToken) =
        withSegment "verificationId" verificationId (fun escaped -> attempt ct (client.V1.CancelVerification(escaped, ct)))

/// The kinds of supporting document Vouchsafe verifies.
[<RequireQualifiedAccess>]
type SupportingDocumentType =
    | Payslip
    | PensionAnnualStatement
    | BenefitsLetter
    | HmppsLetter
    | NhsLetter
    | BankStatement
    | CreditCardStatement
    | MortgageStatement
    | UtilityBill
    | MobilePhoneBill

/// A photo ID kind with what it needs beyond the front image.
[<RequireQualifiedAccess>]
type PhotoId =
    | Passport
    /// MRZ-enabled IDs only.
    | NationalId of back: Upload
    /// GB and EU licences; the issuing country as ISO 3166-1 alpha-2 (GB, DE, FR, …).
    | DrivingLicence of countryCode: string
    /// PASS cards, including Citizencard and Young Scot.
    | PassCard
    | UnfamiliarPhotoId

/// Document checks without a hosted flow (beta at Vouchsafe).
module Documents =

    let private subType kind =
        match kind with
        | SupportingDocumentType.Payslip -> "Payslip"
        | SupportingDocumentType.PensionAnnualStatement -> "PensionAnnualStatement"
        | SupportingDocumentType.BenefitsLetter -> "BenefitsLetter"
        | SupportingDocumentType.HmppsLetter -> "HMPPSLetter"
        | SupportingDocumentType.NhsLetter -> "NHSLetter"
        | SupportingDocumentType.BankStatement -> "BankStatement"
        | SupportingDocumentType.CreditCardStatement -> "CreditCardStatement"
        | SupportingDocumentType.MortgageStatement -> "MortgageStatement"
        | SupportingDocumentType.UtilityBill -> "UtilityBill"
        | SupportingDocumentType.MobilePhoneBill -> "MobilePhoneBill"

    /// Extracts and validates a supporting document. minimumAgeMonths fails documents older than that many months.
    let verifySupportingDocumentAsync
        (client: VouchsafeClient)
        (kind: SupportingDocumentType)
        (document: Upload)
        (minimumAgeMonths: int voption)
        (ct: CancellationToken)
        : Task<Result<VouchsafeV1.SupportingDocumentVerificationResponse, VouchsafeFailure>> =
        let fields =
            [ yield "sub_type", subType kind
              match minimumAgeMonths with
              | ValueSome months -> yield "minimum_document_length", string months
              | ValueNone -> () ]
        postFormAsync client.V1 "verify/supporting-documents" fields [ "document", document ] ct

    /// Extracts and validates a photo ID, matching its photo to faceScan when given.
    let verifyPhotoIdAsync
        (client: VouchsafeClient)
        (photoId: PhotoId)
        (front: Upload)
        (faceScan: Upload voption)
        (ct: CancellationToken)
        : Task<Result<VouchsafeV1.PhotoIdVerificationResponse, VouchsafeFailure>> =
        let subType, extraFields, extraFiles =
            match photoId with
            | PhotoId.Passport -> "Passport", [], []
            | PhotoId.NationalId back -> "NationalId", [], [ "back", back ]
            | PhotoId.DrivingLicence country -> "DrivingLicence", [ "country_code", country ], []
            | PhotoId.PassCard -> "PASSCard", [], []
            | PhotoId.UnfamiliarPhotoId -> "UnfamiliarPhotoId", [], []
        let files =
            [ yield "front", front
              yield! extraFiles
              match faceScan with
              | ValueSome scan -> yield "face_scan", scan
              | ValueNone -> () ]
        postFormAsync client.V1 "verify/photo-id" (("sub_type", subType) :: extraFields) files ct

    /// Checks an eVisa share code; input is a VouchsafeV1.ImmigrationStatusInput, RightToWorkInput or RightToRentInput.
    let verifyEvisaAsync (client: VouchsafeClient) (input: obj) (ct: CancellationToken) =
        attempt ct (client.V1.VerifyEvisa(input, ct))

/// Teams under a partner account.
module Team =

    let createAsync (client: VouchsafeClient) (partnerToken: string) (input: VouchsafeV1.CreateTeamInput) (ct: CancellationToken) =
        attempt ct (client.V1.CreateTeam(partnerToken, input, ct))

    let getAsync (client: VouchsafeClient) (ct: CancellationToken) = attempt ct (client.V1.GetTeam ct)

/// Smart lookups (data checks without a hosted flow) and postcode search.
module SmartLookups =

    let performAsync (client: VouchsafeClient) (input: VouchsafeV1.SmartLookupInput) (ct: CancellationToken) =
        attempt ct (client.V1.PerformSmartLookup(input, ct))

    /// Addresses at a postcode, in the shape the risk check's address takes.
    let searchPostcodeAsync (client: VouchsafeClient) (postcode: string) (ct: CancellationToken) =
        attempt ct (client.V1.SearchPostcode(postcode, ct))

module Flows =

    let listAsync (client: VouchsafeClient) (ct: CancellationToken) = attempt ct (client.V1.ListFlows ct)

    let getAsync (client: VouchsafeClient) (flowId: string) (ct: CancellationToken) =
        withSegment "flowId" flowId (fun escaped -> attempt ct (client.V1.GetFlow(escaped, ct)))

module Coverage =

    /// Supported digital IDs, photo IDs, supporting documents and data sources, optionally for one country or kind.
    let listAsync (client: VouchsafeClient) (countryCode: string voption) (kind: VouchsafeV1.CoverageType voption) (ct: CancellationToken) =
        attempt ct (client.V1.ListCoverage(ValueOption.toOption countryCode, ValueOption.toOption kind, ct))

module Artefacts =

    /// A temporary download link for an artefact (document image, report, …).
    let getAsync (client: VouchsafeClient) (artefactKey: string) (ct: CancellationToken) =
        withSegment "artefactKey" artefactKey (fun escaped -> attempt ct (client.V1.GetArtefact(escaped, ct)))

/// Ongoing monitoring alerts.
module Alerts =

    /// Monitored accounts, optionally only those with the status; pass the previous answer's cursor for the next page.
    let listAccountsAsync
        (client: VouchsafeClient)
        (status: VouchsafeV1.AlertAccountStatus voption)
        (cursor: string voption)
        (ct: CancellationToken)
        =
        attempt ct (client.V1.ListAccounts(ValueOption.toOption status, ValueOption.toOption cursor, ct))

    let getAccountAsync (client: VouchsafeClient) (accountId: string) (ct: CancellationToken) =
        withSegment "accountId" accountId (fun escaped -> attempt ct (client.V1.GetAccountDetail(escaped, ct)))

    /// Turns ongoing monitoring on or off for an account.
    let setAlertsAsync (client: VouchsafeClient) (accountId: string) (enabled: bool) (ct: CancellationToken) =
        withSegment "accountId" accountId (fun escaped ->
            attempt ct (client.V1.ToggleAlerts(escaped, VouchsafeV1.ToggleAlertsInput(AlertsEnabled = enabled), ct)))

    let acknowledgeAsync (client: VouchsafeClient) (alertId: string) (ct: CancellationToken) =
        withSegment "alertId" alertId (fun escaped -> attempt ct (client.V1.AcknowledgeAlert(escaped, ct)))

module AdverseMedia =

    let checkAsync (client: VouchsafeClient) (input: VouchsafeV1.AdverseMediaInput) (ct: CancellationToken) =
        attempt ct (client.V1.PerformAdverseMediaCheck(input, ct))

/// Optional enrichments of a risk check.
[<RequireQualifiedAccess>]
type Enrichment =
    /// Identity and address evidence from official and credit sources; needs an address.
    | CreditBureau
    | OnlineFootprint
    /// Sanctions, watchlists and PEPs; also enrols the person in ongoing monitoring.
    | Aml
    | AdverseMedia

/// A structured address, as GET /smart-lookups/postcode returns it. Needs a residence number or a building name.
type RiskAddress =
    { ResidenceNumber: string voption
      BuildingName: string voption
      Street: string voption
      City: string
      Postcode: string
      Region: string voption
      CountryCode: string }

/// What a risk check sends. Thresholds are 0-100; Vouchsafe's defaults are 50 (online footprint) and 96 (AML).
type RiskCheck =
    { FirstName: string
      LastName: string
      Email: string
      DateOfBirth: DateOnly
      /// E.164 (+447700900123); numbers without a country code are read as UK numbers.
      Phone: string voption
      Enrichments: Enrichment list
      Address: RiskAddress voption
      OnlineFootprintThreshold: int voption
      AmlThreshold: int voption
      /// A live selfie or liveness image (JPEG or PNG, up to 10 MB), checked for reuse under other identities.
      FaceScan: Upload voption }

module RiskCheck =

    /// A check of the four required fields, with no enrichments.
    let create firstName lastName email dateOfBirth =
        { FirstName = firstName
          LastName = lastName
          Email = email
          DateOfBirth = dateOfBirth
          Phone = ValueNone
          Enrichments = []
          Address = ValueNone
          OnlineFootprintThreshold = ValueNone
          AmlThreshold = ValueNone
          FaceScan = ValueNone }

    let private enrichmentCode enrichment =
        match enrichment with
        | Enrichment.CreditBureau -> "CREDIT_BUREAU"
        | Enrichment.OnlineFootprint -> "ONLINE_FOOTPRINT"
        | Enrichment.Aml -> "AML"
        | Enrichment.AdverseMedia -> "ADVERSE_MEDIA"

    let private orNull (value: string voption) = ValueOption.toObj value

    let private problems (check: RiskCheck) =
        [ if [ check.FirstName; check.LastName; check.Email ] |> List.exists String.IsNullOrWhiteSpace then
              "first name, last name and email are required"
          if List.contains Enrichment.CreditBureau check.Enrichments && check.Address.IsNone then
              "the credit bureau enrichment needs an address"
          match check.Address with
          | ValueSome address when address.ResidenceNumber.IsNone && address.BuildingName.IsNone ->
              "an address needs a residence number or a building name"
          | ValueSome address when [ address.City; address.Postcode; address.CountryCode ] |> List.exists String.IsNullOrWhiteSpace ->
              "an address needs a city, postcode and country code"
          | _ -> ()
          for threshold in [ check.OnlineFootprintThreshold; check.AmlThreshold ] do
              match threshold with
              | ValueSome value when value < 0 || value > 100 -> "thresholds are 0-100"
              | _ -> () ]

    /// The multipart fields of a check, or why it cannot be sent.
    let internal fields (check: RiskCheck) =
        match problems check with
        | [] ->
            Ok
                [ yield "first_name", check.FirstName
                  yield "last_name", check.LastName
                  yield "email", check.Email
                  yield "date_of_birth", check.DateOfBirth.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)
                  match check.Phone with
                  | ValueSome phone -> yield "phone", phone
                  | ValueNone -> ()
                  match check.Enrichments |> List.distinct with
                  | [] -> ()
                  | enrichments -> yield "enrichments", JsonSerializer.Serialize(enrichments |> List.map enrichmentCode)
                  match check.Address with
                  | ValueSome address ->
                      yield
                          "address",
                          JsonSerializer.Serialize
                              {| residence_number = orNull address.ResidenceNumber
                                 building_name = orNull address.BuildingName
                                 street = orNull address.Street
                                 city = address.City
                                 postcode = address.Postcode
                                 region = orNull address.Region
                                 country_code = address.CountryCode |}
                  | ValueNone -> ()
                  match check.OnlineFootprintThreshold, check.AmlThreshold with
                  | ValueNone, ValueNone -> ()
                  | footprint, aml ->
                      let thresholds =
                          [ match footprint with
                            | ValueSome value -> "online_footprint", value
                            | ValueNone -> ()
                            match aml with
                            | ValueSome value -> "aml", value
                            | ValueNone -> () ]
                          |> dict
                      yield "thresholds", JsonSerializer.Serialize thresholds ]
        | reasons -> Error(InvalidRequest(String.concat "; " reasons))

/// The v2 risk check (experimental at Vouchsafe): a risk score from 0 to 100 and a CLEAR, REVIEW or REJECT
/// recommendation, with the enrichments requested.
module Risk =

    let checkAsync (client: VouchsafeClient) (check: RiskCheck) (ct: CancellationToken) : Task<Result<VouchsafeV2.GetRiskResponse, VouchsafeFailure>> =
        match RiskCheck.fields check with
        | Error failure -> Task.FromResult(Error failure)
        | Ok fields ->
            let files =
                match check.FaceScan with
                | ValueSome scan -> [ "face_scan", scan ]
                | ValueNone -> []
            postFormAsync client.V2 "risk" fields files ct
