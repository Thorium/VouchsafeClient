module TokenTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Vouchsafe
open FakeVouchsafe

let private team (client: VouchsafeClient) = Api.Team.getAsync client ct

[<Fact>]
let ``authentication posts the client id and secret, and calls carry the bearer token`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 200, "{}"))
        let! result = team (clientWith handler)
        expectOk result |> ignore
        let token = Assert.Single handler.TokenCalls
        Assert.Equal("POST", token.Method)
        Assert.Equal("https://app.vouchsafe.id/api/v1/authenticate", token.Uri)
        let body = JsonDocument.Parse(token.Body).RootElement
        Assert.Equal("client-id", body.GetProperty("client_id").GetString())
        Assert.Equal("client-secret", body.GetProperty("client_secret").GetString())
        let api = Assert.Single handler.ApiCalls
        Assert.Equal("https://app.vouchsafe.id/api/v1/team", api.Uri)
        Assert.Equal("Bearer tok-1", api.Headers["Authorization"])
    }

[<Fact>]
let ``the token is kept until five minutes before expires_at`` () : Task =
    task {
        let start = DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)
        let clock = ref start
        let respond (call: Call) =
            if isToken call then 201, tokenJson "tok" (start.AddHours 24.) else 200, "{}"
        use handler = new FakeVouchsafeHandler(respond)
        let client = clientAt clock handler
        let! _ = team client
        clock.Value <- start.AddHours 23.9
        let! _ = team client
        Assert.Equal(1, handler.TokenCalls.Length)
        clock.Value <- start.AddHours 23.95
        let! _ = team client
        Assert.Equal(2, handler.TokenCalls.Length)
    }

[<Fact>]
let ``concurrent first calls share one authentication`` () : Task =
    task {
        use handler = new FakeVouchsafeHandler(respondWith (fun _ -> 200, "{}"))
        let client = clientWith handler
        let! results = Task.WhenAll [ for _ in 1..20 -> team client ]
        results |> Array.iter (expectOk >> ignore)
        Assert.Equal(1, handler.TokenCalls.Length)
    }

[<Fact>]
let ``a 401 re-authenticates and sends the request once more`` () : Task =
    task {
        let api (call: Call) = if call.Headers["Authorization"] = "Bearer tok-1" then 401, """{"message":"expired"}""" else 200, "{}"
        use handler = new FakeVouchsafeHandler(respondWith api)
        let! result = team (clientWith handler)
        expectOk result |> ignore
        Assert.Equal<string list>([ "Bearer tok-1"; "Bearer tok-2" ], handler.ApiCalls |> List.map (fun c -> c.Headers["Authorization"]))
    }

[<Fact>]
let ``refused credentials are AuthenticationRefused and nothing else is sent`` () : Task =
    task {
        let respond (call: Call) = if isToken call then 401, """{"message":"Invalid client credentials"}""" else 200, "{}"
        use handler = new FakeVouchsafeHandler(respond)
        let! result = team (clientWith handler)
        match result with
        | Error(AuthenticationRefused(401, body)) -> Assert.Contains("Invalid client credentials", body)
        | other -> failwith $"%A{other}"
        Assert.Empty handler.ApiCalls
    }

[<Fact>]
let ``expires_at is read, and a missing or past one means 24 hours`` () =
    let now = DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)
    let expiry json = (Token.parse now json).Value.ExpiresAt
    Assert.Equal(DateTimeOffset(2026, 10, 29, 9, 30, 0, TimeSpan.Zero), expiry """{"access_token":"a","expires_at":"2026-10-29T09:30:00.000Z"}""")
    Assert.Equal(now.AddHours 24., expiry """{"access_token":"a"}""")
    Assert.Equal(now.AddHours 24., expiry """{"access_token":"a","expires_at":"2020-01-01T00:00:00Z"}""")
    Assert.True((Token.parse now """{"access_token":""}""").IsNone)
    Assert.True((Token.parse now "not json").IsNone)

[<Fact>]
let ``printing credentials or a token hides the secrets`` () =
    let token = { Value = "secret-token"; ExpiresAt = DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero) }
    for text in [ $"%A{credentials}"; string credentials; $"%A{token}"; string token ] do
        Assert.DoesNotContain("client-secret", text)
        Assert.DoesNotContain("secret-token", text)
    Assert.Contains("client-id", string credentials)
