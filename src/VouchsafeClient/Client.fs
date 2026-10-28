namespace Vouchsafe

open System
open System.Globalization
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// The client id and secret from the API Integration tab of the Vouchsafe dashboard. A sandbox secret puts every call
/// that supports it in sandbox mode. Printing one (%A, ToString) shows the client id only.
[<StructuredFormatDisplay("{Display}")>]
type Credentials =
    { ClientId: string
      ClientSecret: string }

    member this.Display = $"{{ ClientId = {this.ClientId}; ClientSecret hidden }}"
    override this.ToString() = this.Display

/// Base addresses of the two API versions. A missing trailing '/' is added when the client is created.
type Endpoints = { V1: Uri; V2: Uri }

module Endpoints =

    /// The address with a trailing '/', so that relative paths resolve under it rather than replacing its last segment.
    let internal asBase (address: Uri) =
        if address.AbsolutePath.EndsWith "/" then address
        else UriBuilder(address, Path = address.AbsolutePath + "/").Uri

    /// app.vouchsafe.id, for both live and sandbox credentials.
    let standard =
        { V1 = Uri "https://app.vouchsafe.id/api/v1/"
          V2 = Uri "https://app.vouchsafe.id/api/v2/" }

type VouchsafeFailure =
    /// The request was not sent: an argument cannot be valid.
    | InvalidRequest of reason: string
    /// Vouchsafe answered with a non-success status; body is the response text.
    | ApiError of httpStatus: int * body: string
    /// The authenticate endpoint refused the credentials, or answered without an access_token.
    | AuthenticationRefused of httpStatus: int * body: string
    /// The request was not answered, or the answer could not be read.
    | TransportError of exn

module VouchsafeFailure =

    /// One line saying what went wrong, for logs and exception messages.
    let describe failure =
        match failure with
        | InvalidRequest reason -> $"Not sent: {reason}"
        | ApiError(status, "") -> $"Vouchsafe answered HTTP {status}"
        | ApiError(status, body) -> $"Vouchsafe answered HTTP {status}: {body}"
        | AuthenticationRefused(status, body) -> $"Vouchsafe refused the credentials (HTTP {status}): {body}"
        | TransportError e -> $"Request failed: {e.Message}"

/// Raised inside a call when authentication is refused; calls return it as AuthenticationRefused.
exception AuthenticationRefusedException of httpStatus: int * body: string

/// A bearer token and the time after which it is not used. Printing one shows the expiry only.
[<StructuredFormatDisplay("{Display}")>]
type AccessToken =
    { Value: string
      ExpiresAt: DateTimeOffset }

    member this.Display = $"{{ Value hidden; ExpiresAt = {this.ExpiresAt:o} }}"
    override this.ToString() = this.Display

module internal Token =

    /// Tokens are renewed this long before they expire, so a request never carries one that expires in flight.
    let renewBefore = TimeSpan.FromMinutes 5.

    /// Vouchsafe documents 24-hour tokens; used when expires_at is missing or unreadable.
    let defaultLifetime = TimeSpan.FromHours 24.

    let request (credentials: Credentials) (authenticateUri: Uri) =
        let body = JsonSerializer.Serialize {| client_id = credentials.ClientId; client_secret = credentials.ClientSecret |}
        let request = new HttpRequestMessage(HttpMethod.Post, authenticateUri)
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
        request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        request

    /// The token in a 2xx authenticate response; expires_at is used when it is a readable time after `now`.
    let parse (now: DateTimeOffset) (text: string) =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement
            match root.TryGetProperty "access_token" with
            | true, token when token.ValueKind = JsonValueKind.String && token.GetString() <> "" ->
                let expiresAt =
                    match root.TryGetProperty "expires_at" with
                    | true, expires when expires.ValueKind = JsonValueKind.String ->
                        match DateTimeOffset.TryParse(expires.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
                        | true, at when at > now -> at
                        | _ -> now + defaultLifetime
                    | _ -> now + defaultLifetime
                ValueSome { Value = token.GetString(); ExpiresAt = expiresAt }
            | _ -> ValueNone
        with :? JsonException -> ValueNone

/// Fetches the bearer token and keeps it until five minutes before it expires; concurrent callers share one fetch.
[<Sealed>]
type TokenSource internal (credentials: Credentials, authenticateUri: Uri, invoker: HttpMessageInvoker, now: unit -> DateTimeOffset) =
    let gate = new SemaphoreSlim(1, 1)

    /// A reference cell, not a voption: it is read outside the gate, and a struct read could be torn.
    let mutable current: AccessToken option = None

    let usable () =
        match current with
        | Some token when token.ExpiresAt - Token.renewBefore > now () -> ValueSome token
        | _ -> ValueNone

    let fetch (ct: CancellationToken) =
        task {
            use request = Token.request credentials authenticateUri
            use! response = invoker.SendAsync(request, ct)
            let! text = response.Content.ReadAsStringAsync ct
            let status = int response.StatusCode
            return
                if not response.IsSuccessStatusCode then
                    raise (AuthenticationRefusedException(status, text))
                else
                    match Token.parse (now ()) text with
                    | ValueSome token -> token
                    | ValueNone -> raise (AuthenticationRefusedException(status, text))
        }

    /// A valid token, fetched if there is none or it is about to expire.
    member _.GetAsync(ct: CancellationToken) : Task<AccessToken> =
        match usable () with
        | ValueSome token -> Task.FromResult token
        | ValueNone ->
            task {
                do! gate.WaitAsync ct
                try
                    match usable () with
                    | ValueSome token -> return token
                    | ValueNone ->
                        let! token = fetch ct
                        current <- Some token
                        return token
                finally
                    gate.Release() |> ignore
            }

    /// Drops the token if it is still the cached one, so the next GetAsync fetches a new one.
    member _.Invalidate(stale: AccessToken) =
        match current with
        | Some token when obj.ReferenceEquals(token, stale) -> current <- None
        | _ -> ()

/// Adds the bearer token to every request; a 401 drops the token and the request is sent once more with a fresh one.
type internal BearerHandler(tokens: TokenSource, inner: HttpMessageHandler) =
    inherit DelegatingHandler(inner)

    member private _.SendInner(request, ct) = base.SendAsync(request, ct)

    override this.SendAsync(request, ct) =
        task {
            let! token = tokens.GetAsync ct
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token.Value)
            let! response = this.SendInner(request, ct)
            if response.StatusCode <> HttpStatusCode.Unauthorized then
                return response
            else
                response.Dispose()
                tokens.Invalidate token
                let! fresh = tokens.GetAsync ct
                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", fresh.Value)
                return! this.SendInner(request, ct)
        }

/// A Vouchsafe connection: one token cache and the generated client of each API version over one shared handler.
/// Keep it for the application's lifetime; the handler stays the caller's (it is never disposed here).
[<Sealed>]
type VouchsafeClient internal (credentials: Credentials, configured: Endpoints, handler: HttpMessageHandler, now: unit -> DateTimeOffset) =
    let endpoints = { V1 = Endpoints.asBase configured.V1; V2 = Endpoints.asBase configured.V2 }
    let tokens = TokenSource(credentials, Uri(endpoints.V1, "authenticate"), new HttpMessageInvoker(handler, false), now)
    let authorized = new BearerHandler(tokens, handler)

    /// Unset optional fields are left out of request bodies rather than sent as null.
    let json = JsonSerializerOptions(DefaultIgnoreCondition = Serialization.JsonIgnoreCondition.WhenWritingNull)

    let http (baseAddress: Uri) = new HttpClient(authorized, false, BaseAddress = baseAddress)
    let v1 = lazy VouchsafeV1.Client(http endpoints.V1, json)
    let v2 = lazy VouchsafeV2.Client(http endpoints.V2, json)

    /// A client over a caller-owned handler, such as one shared SocketsHttpHandler.
    new(credentials, endpoints, handler) = VouchsafeClient(credentials, endpoints, handler, fun () -> DateTimeOffset.UtcNow)

    member _.Endpoints = endpoints
    member _.Tokens = tokens
    member _.V1 = v1.Value
    member _.V2 = v2.Value

    /// Reads a union-typed value (generated as obj, a JsonElement at run time) as one of its member types, such as
    /// VouchsafeV1.PassportExtractedDetails; ValueNone when it is absent or does not read as that type.
    member _.As<'T>(value: obj) : 'T voption =
        match value with
        | :? JsonElement as element when element.ValueKind <> JsonValueKind.Null && element.ValueKind <> JsonValueKind.Undefined ->
            try
                match element.Deserialize<'T> json with
                | result when isNull (box result) -> ValueNone
                | result -> ValueSome result
            with :? JsonException -> ValueNone
        | :? 'T as typed -> ValueSome typed
        | _ -> ValueNone

    /// A handler for app.vouchsafe.id. Create it once per application.
    static member CreateHandler() : HttpMessageHandler =
        new SocketsHttpHandler(UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes 5.)

    /// A client with its own handler.
    static member Create(credentials, endpoints) = VouchsafeClient(credentials, endpoints, VouchsafeClient.CreateHandler())

module VouchsafeClient =

    /// A client over a caller-owned handler.
    let create credentials endpoints (handler: HttpMessageHandler) = VouchsafeClient(credentials, endpoints, handler)

/// How the Api functions run a call and report its failure.
module internal Call =

    [<TailCall>]
    let rec private failureOf (e: exn) =
        match e with
        | :? AggregateException as e when e.InnerExceptions.Count = 1 -> failureOf e.InnerException
        | :? Swagger.OpenApiException as e -> ApiError(e.StatusCode, e.ResponseBody)
        | AuthenticationRefusedException(status, body) -> AuthenticationRefused(status, body)
        | e -> TransportError e

    /// Runs a call; exceptions become Error. Cancellation of the token propagates.
    let guarded (ct: CancellationToken) (run: unit -> Task<Result<'T, VouchsafeFailure>>) =
        task {
            try
                return! run ()
            with error when not (ct.IsCancellationRequested && (error :? OperationCanceledException)) ->
                return Error(failureOf error)
        }

    /// Runs a generated call.
    let attempt (ct: CancellationToken) (call: Async<'T>) =
        guarded ct (fun () -> task {
            let! value = Async.StartAsTask(call, cancellationToken = ct)
            return Ok value })

    /// A path parameter as one escaped segment. The generated clients insert path values as they are, so "../" or "?"
    /// would reach another endpoint with the bearer token; empty, "." and ".." are refused.
    let segment (name: string) (value: string) =
        match value with
        | null
        | ""
        | "."
        | ".." -> Error(InvalidRequest $"{name} must be a non-empty identifier")
        | _ -> Ok(Uri.EscapeDataString value)

    /// Runs send with the escaped path parameter, or returns InvalidRequest without sending.
    let withSegment name value (send: string -> Task<Result<'T, VouchsafeFailure>>) =
        match segment name value with
        | Ok escaped -> send escaped
        | Error failure -> Task.FromResult(Error failure)
