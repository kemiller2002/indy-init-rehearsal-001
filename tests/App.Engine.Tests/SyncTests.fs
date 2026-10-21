module App.Engine.Tests.SyncTests

open System
open System.Text
open Xunit
open IndyInitRootCauseInvestigator.Engine
open App.Engine.Tests.CodecTests

let private token = "ghu_SECRETtoken0123456789abcdef"

let private config =
    { Owner = "kemiller2002"
      Repository = "rci-data"
      Branch = "main"
      Token = token }

let private eventsJson = EventCodec.encode Scenario.iid Scenario.closedHistory
let private path = "investigations/" + Id.text Scenario.iid + "/events.json"

let private header name (request: HttpRequest) =
    request.Headers |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

let private bodyOf (request: HttpRequest) =
    match request.Body with
    | Some b -> parseObject b
    | None -> failwith "expected a body"

[<Fact>]
let ``R10.2 events are stored as a human-readable JSON file per investigation`` () =
    Assert.Equal(path, GitHub.contentPath Scenario.iid)

[<Fact>]
let ``R10.1 the read request is a GET of the contents API on the branch`` () =
    let request = GitHub.readRequest config Scenario.iid
    Assert.Equal("GET", request.Method)
    Assert.Equal("https://api.github.com/repos/kemiller2002/rci-data/contents/" + path + "?ref=main", request.Url)
    Assert.Equal(Some("Bearer " + token), header "Authorization" request)
    Assert.Equal(Some "application/vnd.github+json", header "Accept" request)
    Assert.Equal(Some "2022-11-28", header "X-GitHub-Api-Version" request)
    Assert.Equal(None, request.Body)

[<Fact>]
let ``R10.3 the write request is a PUT with message, base64 UTF-8 content, branch and sha`` () =
    let request = GitHub.writeRequest config Scenario.iid eventsJson (Some "abc123") "rci: append events 1-5"
    Assert.Equal("PUT", request.Method)
    Assert.Equal("https://api.github.com/repos/kemiller2002/rci-data/contents/" + path, request.Url)
    Assert.Equal(Some("Bearer " + token), header "Authorization" request)
    Assert.Equal(Some "application/vnd.github+json", header "Accept" request)
    Assert.Equal(Some "2022-11-28", header "X-GitHub-Api-Version" request)
    let body = bodyOf request
    Assert.Equal("rci: append events 1-5", textAt [ "message" ] body)
    Assert.Equal("main", textAt [ "branch" ] body)
    Assert.Equal("abc123", textAt [ "sha" ] body)
    let decoded = Encoding.UTF8.GetString(Convert.FromBase64String(textAt [ "content" ] body))
    Assert.Equal(eventsJson, decoded)

[<Fact>]
let ``R10.4 a first write omits the sha`` () =
    let body = bodyOf (GitHub.writeRequest config Scenario.iid eventsJson None "create")
    Assert.False(body.ContainsKey "sha")

[<Fact>]
let ``R9.9 R11.3 the token appears only in the Authorization header`` () =
    let requests =
        [ GitHub.readRequest config Scenario.iid
          GitHub.writeRequest config Scenario.iid eventsJson (Some "abc") "message"
          GitHub.writeRequest config Scenario.iid eventsJson None "message" ]
    for request in requests do
        Assert.DoesNotContain(token, request.Url)
        Assert.DoesNotContain(token, request.Body |> Option.defaultValue "")
        let body = request.Body |> Option.map parseObject
        body
        |> Option.iter (fun b ->
            let content = Encoding.UTF8.GetString(Convert.FromBase64String(textAt [ "content" ] b))
            Assert.DoesNotContain(token, content))
        for name, value in request.Headers do
            if name <> "Authorization" then Assert.DoesNotContain(token, value)
    Assert.DoesNotContain(token, eventsJson)

[<Fact>]
let ``R11.3 a token accidentally present in content or message is redacted before commit`` () =
    let request = GitHub.writeRequest config Scenario.iid ("{\"leak\":\"" + token + "\"}") None ("msg " + token)
    let body = bodyOf request
    let content = Encoding.UTF8.GetString(Convert.FromBase64String(textAt [ "content" ] body))
    Assert.DoesNotContain(token, content)
    Assert.DoesNotContain(token, textAt [ "message" ] body)

[<Fact>]
let ``R10.1 interpretRead decodes 200 and treats 404 as nothing stored`` () =
    // GitHub wraps base64 at 60 characters with newlines.
    let wrapped =
        Convert.ToBase64String(Encoding.UTF8.GetBytes eventsJson)
        |> Seq.chunkBySize 60
        |> Seq.map String
        |> String.concat "\n"
    let body = "{\"type\":\"file\",\"sha\":\"blob42\",\"encoding\":\"base64\",\"content\":\"" + wrapped.Replace("\n", "\\n") + "\"}"
    Assert.Equal(Ok(Some("blob42", eventsJson)), GitHub.interpretRead 200 body)
    Assert.Equal(Ok None, GitHub.interpretRead 404 "{\"message\":\"Not Found\"}")
    Assert.True(Result.isError (GitHub.interpretRead 401 "{\"message\":\"Bad credentials\"}"))
    Assert.True(Result.isError (GitHub.interpretRead 500 "oops"))
    Assert.True(Result.isError (GitHub.interpretRead 200 "{\"sha\":\"x\",\"content\":\"***\"}"))

[<Fact>]
let ``R10.4 R10.6 interpretWrite distinguishes written, conflict, auth and failure`` () =
    Assert.Equal(Written "newsha", GitHub.interpretWrite 200 "{\"content\":{\"sha\":\"newsha\"},\"commit\":{\"sha\":\"c1\"}}")
    Assert.Equal(Written "created", GitHub.interpretWrite 201 "{\"content\":{\"sha\":\"created\"}}")
    match GitHub.interpretWrite 409 "{\"message\":\"is at abc but expected def\"}" with
    | RemoteConflict detail -> Assert.Contains("expected def", detail)
    | other -> failwithf "expected conflict, got %A" other
    Assert.True(match GitHub.interpretWrite 422 "{\"message\":\"sha wasn't supplied\"}" with RemoteConflict _ -> true | _ -> false)
    Assert.True(match GitHub.interpretWrite 401 "{\"message\":\"Bad credentials\"}" with AuthFailed _ -> true | _ -> false)
    Assert.True(match GitHub.interpretWrite 403 "{}" with AuthFailed _ -> true | _ -> false)
    Assert.True(match GitHub.interpretWrite 500 "" with WriteFailed _ -> true | _ -> false)
    Assert.True(match GitHub.interpretWrite 200 "not json" with WriteFailed _ -> true | _ -> false)

[<Fact>]
let ``R10.6 only Synced counts as remote confirmation`` () =
    Assert.True(SyncStatus.isRemoteConfirmed Synced)
    for s in [ LocalOnly; Pending; Sending; Conflicted "x"; SyncUnknown ] do
        Assert.False(SyncStatus.isRemoteConfirmed s)

let private interim = Scenario.interimHistory
let private closed = Scenario.closedHistory

[<Fact>]
let ``R10.4 reconcile: equal histories are up to date`` () =
    Assert.Equal(Reconcile.UpToDate, Reconcile.plan closed closed)
    Assert.Equal(Reconcile.UpToDate, Reconcile.plan [] [])

[<Fact>]
let ``R10.4 reconcile: local ahead pushes local`` () =
    Assert.Equal(Reconcile.PushLocal, Reconcile.plan closed interim)
    Assert.Equal(Reconcile.PushLocal, Reconcile.plan closed [])

[<Fact>]
let ``R10.4 reconcile: remote ahead adopts remote`` () =
    Assert.Equal(Reconcile.AdoptRemote, Reconcile.plan interim closed)
    Assert.Equal(Reconcile.AdoptRemote, Reconcile.plan [] closed)

[<Fact>]
let ``R10.4 reconcile: divergent histories are never overwritten`` () =
    let n = interim.Length
    let remote = interim @ [ { closed[n] with CommandId = Guid.NewGuid() } ]
    let local = closed |> List.take (n + 1)
    match Reconcile.plan local remote with
    | Reconcile.Diverged detail -> Assert.Contains(string (n + 1), detail)
    | other -> failwithf "expected divergence, got %A" other
    // Same sequence and command id but rewritten content is also divergence.
    let rewritten = interim |> List.mapi (fun i e -> if i = 3 then { e with At = e.At.AddSeconds 1.0 } else e)
    Assert.True(match Reconcile.plan interim rewritten with Reconcile.Diverged _ -> true | _ -> false)

[<Fact>]
let ``R10.4 reconcile survives a codec round trip`` () =
    let _, remote = EventCodec.decode (EventCodec.encode Scenario.iid interim) |> Result.defaultWith failwith
    Assert.Equal(Reconcile.UpToDate, Reconcile.plan interim remote)
