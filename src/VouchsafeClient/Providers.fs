/// Generated clients for the Vouchsafe API, one per API version.
[<AutoOpen>]
module Vouchsafe.Providers

open SwaggerProvider

let [<Literal>] private schemas = __SOURCE_DIRECTORY__ + "/Schemas/"

/// Vouchsafe API v1: verifications, document verification, smart lookups, flows, coverage, alerts, adverse media.
type VouchsafeV1 = OpenApiClientProvider<const(schemas + "swagger.json"), PreferAsync=true>

/// Vouchsafe API v2 (experimental): risk checks.
type VouchsafeV2 = OpenApiClientProvider<const(schemas + "swagger-v2.json"), PreferAsync=true>
