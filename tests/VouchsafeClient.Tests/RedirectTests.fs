/// Tokens and bodies stay on the client's own hosts.
module RedirectTests

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Xunit
open Vouchsafe
open FakeVouchsafe

/// Answers as respond says, and records each request as it arrives, before respond can change it.
type private Recording(respond: HttpRequestMessage -> HttpStatusCode) =
    inherit HttpMessageHandler()
    let seen = Collections.Generic.List<string * string>()

    /// Host and Authorization header of each request that wasn't a sign-in.
    member _.Seen = List.ofSeq seen

    override _.SendAsync(request, _) =
        if request.RequestUri.AbsolutePath.EndsWith "/authenticate" then
            let token = tokenJson "the-token" (DateTimeOffset.UtcNow.AddDays 1.)
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created, Content = new StringContent(token)))
        else
            let authorization = if isNull request.Headers.Authorization then "" else string request.Headers.Authorization
            seen.Add((request.RequestUri.Host, authorization))
            let status = respond request
            Task.FromResult(new HttpResponseMessage(status, Content = new StringContent "{}"))

[<Fact>]
let ``the handler made here doesn't follow redirects`` () =
    Assert.False((VouchsafeClient.CreateHandler() :?> SocketsHttpHandler).AllowAutoRedirect)

/// A caller's handler that follows redirects clears the token and moves the request; a 401 from there must not bring the token back.
[<Fact>]
let ``a 401 after a redirect doesn't send a new token to the other host`` () : Task =
    task {
        let handler =
            new Recording(fun request ->
                request.RequestUri <- Uri "https://evil.example/x"
                request.Headers.Authorization <- null
                HttpStatusCode.Unauthorized)
        let client = VouchsafeClient.create credentials Endpoints.standard handler
        let! result = Api.Team.getAsync client ct
        match result with
        | Error(InvalidRequest reason) -> Assert.Contains("evil.example", reason)
        | other -> failwith $"Unexpected: {other}"
        Assert.Equal<(string * string) list>([ "app.vouchsafe.id", "Bearer the-token" ], handler.Seen)
    }

[<Fact>]
let ``an endpoint set by hand is one the token may go to`` () : Task =
    task {
        let handler = new Recording(fun _ -> HttpStatusCode.OK)
        let endpoints = { V1 = Uri "https://vouchsafe.example.com/v1/"; V2 = Uri "https://vouchsafe.example.com/v2/" }
        let client = VouchsafeClient.create credentials endpoints handler
        let! result = Api.Team.getAsync client ct
        expectOk result |> ignore
        Assert.Equal<(string * string) list>([ "vouchsafe.example.com", "Bearer the-token" ], handler.Seen)
    }
