module FakeVouchsafe

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Vouchsafe

type Call =
    { Method: string
      /// Absolute URI as sent.
      Uri: string
      Body: string
      Headers: Map<string, string> }

/// Answers each request with respond call, and records the calls; safe for concurrent requests.
type FakeVouchsafeHandler(respond: Call -> int * string) =
    inherit HttpMessageHandler()
    let calls = Collections.Concurrent.ConcurrentQueue<Call>()

    member _.Calls = List.ofSeq calls
    member this.ApiCalls = this.Calls |> List.filter (fun c -> not (c.Uri.EndsWith "/authenticate"))
    member this.TokenCalls = this.Calls |> List.filter (fun c -> c.Uri.EndsWith "/authenticate")

    override _.SendAsync(request, ct) =
        task {
            ct.ThrowIfCancellationRequested()
            let! body = if isNull request.Content then Task.FromResult "" else request.Content.ReadAsStringAsync ct
            let headers =
                Seq.append request.Headers (if isNull request.Content then Seq.empty else request.Content.Headers)
                |> Seq.map (fun h -> h.Key, String.Join(",", h.Value))
                |> Map.ofSeq
            let call = { Method = request.Method.Method; Uri = request.RequestUri.AbsoluteUri; Body = body; Headers = headers }
            calls.Enqueue call
            let status, json = respond call
            let response = new HttpResponseMessage(enum<HttpStatusCode> status)
            if json <> "" then
                response.Content <- new StringContent(json, Encoding.UTF8, "application/json")
            return response
        }

let credentials = { ClientId = "client-id"; ClientSecret = "client-secret" }

let tokenJson (accessToken: string) (expiresAt: DateTimeOffset) =
    let at = expiresAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", Globalization.CultureInfo.InvariantCulture) + "Z"
    $"""{{"access_token":"{accessToken}","expires_at":"{at}"}}"""

let isToken (call: Call) = call.Uri.EndsWith "/authenticate"

/// Token calls get tok-1, tok-2, ... valid for a day from now; API calls go to api.
let respondWith (api: Call -> int * string) =
    let issued = ref 0
    fun (call: Call) ->
        if isToken call then
            201, tokenJson $"tok-{Interlocked.Increment &issued.contents}" (DateTimeOffset.UtcNow.AddDays 1.)
        else
            api call

let clientWith (handler: FakeVouchsafeHandler) = VouchsafeClient.create credentials Endpoints.standard handler

/// A client whose clock is read from `clock`.
let clientAt (clock: DateTimeOffset ref) (handler: FakeVouchsafeHandler) =
    VouchsafeClient(credentials, Endpoints.standard, handler, fun () -> clock.Value)

let expectOk =
    function
    | Ok v -> v
    | Error e -> failwith (VouchsafeFailure.describe e)

let ct = CancellationToken.None
