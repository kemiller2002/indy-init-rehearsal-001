namespace IndyInitRootCauseInvestigator.Engine

open System
open System.Text
open System.Text.Json.Nodes

/// Where Causa working copies are published (CAUSA-PROV-01). The token is authentication
/// material: it may only ever travel in the Authorization header (R9.9, R11.3).
type RemoteConfig =
    { Owner: string
      Repository: string
      Branch: string
      /// The demonstration run this device writes into (CAUSA-PROV-02); not a secret.
      RunId: string
      Token: string }

/// A pure description of an HTTP call; the shell performs it.
type HttpRequest =
    { Method: string
      Url: string
      Headers: (string * string) list
      Body: string option }
