using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Serilog;
using Apace.ApiServer.Models.Playfab;
using Apace.ApiServer.Services;
using Apace.ApiServer.Utils;
using Apace.Common.Utils;
using Apace.DB;
using Apace.DB.Models.Player;

namespace Apace.ApiServer.Controllers.PlayfabApi;

[Route("Object")]
[Route("20CA2.playfabapi.com/Object")]
[Route("1712756451.playfabapi.com/Object")]
internal sealed class ObjectController : ApaceControllerBase
{
    private sealed record GetObjectsRequest(
        GetObjectsRequest.EntityR Entity,
        object? EscapeObject
    )
    {
        public sealed record EntityR(
            string Id,
            string Type
        );
    }

    private sealed record SetObjectsRequest(
        SetObjectsRequest.EntityR Entity,
        List<SetObjectsRequest.SetObjectR>? Objects
    )
    {
        public sealed record EntityR(
            string Id,
            string Type
        );

        // The client sends more fields (save operation, delete option, ...)
        // but we only care about upserts of the persona profile objects.
        public sealed record SetObjectR(
            string? ObjectName,
            JsonElement? DataObject
        );
    }

    private static readonly string[] DataArray0 = ["#ff523d89", "#0", "#0", "#0"];
    private static readonly string[] DataArray1 = ["#ff2f1f0f", "#0", "#0", "#0"];
    private static readonly string[] DataArray2 = ["#0", "#0", "#ff774235", "#0"];

    [HttpPost("GetObjects")]
    public async Task<Results<ContentHttpResult, ForbidHttpResult, BadRequest>> GetObjects()
    {
        var cancellationToken = Request.HttpContext.RequestAborted;

        var request = await Request.Body.AsJsonAsync<GetObjectsRequest>(cancellationToken);

        if (request is null)
        {
            return TypedResults.BadRequest();
        }

        var tokenUnion = PlayfabAuth();

        if (tokenUnion.IsB)
        {
            return tokenUnion.B.Result is ForbidHttpResult forbid ? forbid : (BadRequest)tokenUnion.B.Result;
        }

        var token = tokenUnion.A;

        if (token.Id != request.Entity.Id || token.Type != request.Entity.Type)
        {
            return TypedResults.Forbid();
        }

        return request.Entity.Type switch
        {
            "master_player_account" => JsonPascalCase(new PlayfabOkResponse(
                    200,
                    "OK",
                    new Dictionary<string, object>()
                    {
                        ["ProfileVersion"] = 172,
                        ["Objects"] = await GetPersonaObjectsAsync(token.Id),
                        ["Entity"] = new Dictionary<string, object>
                        {
                            ["Id"] = request.Entity.Id,
                            ["Type"] = request.Entity.Type,
                            ["TypeString"] = request.Entity.Type,
                        },
                    }
                )),
            _ => TypedResults.BadRequest(),
        };
    }

    [HttpPost("SetObjects")]
    public async Task<Results<Ok, ForbidHttpResult, BadRequest>> SetObjects()
    {
        var cancellationToken = Request.HttpContext.RequestAborted;

        var request = await Request.Body.AsJsonAsync<SetObjectsRequest>(cancellationToken);

        if (request is null)
        {
            return TypedResults.BadRequest();
        }

        var tokenUnion = PlayfabAuth();

        if (tokenUnion.IsB)
        {
            return tokenUnion.B.Result is ForbidHttpResult forbid ? forbid : (BadRequest)tokenUnion.B.Result;
        }

        var token = tokenUnion.A;

        if (token.Id != request.Entity.Id || token.Type != request.Entity.Type)
        {
            return TypedResults.Forbid();
        }

        return request.Entity.Type switch
        {
            "master_player_account" => await HandleSetPersonaObjectsAsync(token.Id, request),
            _ => TypedResults.BadRequest(),
        };
    }

    // Persists persona (skin) profile objects sent by the client so the
    // selected skin survives restarts. Rate limited because these are small
    // but unbounded database writes triggered by client actions.
    private async Task<Results<Ok, ForbidHttpResult, BadRequest>> HandleSetPersonaObjectsAsync(string playerId, SetObjectsRequest request)
    {
        if (request.Objects is null || request.Objects.Count == 0)
        {
            return TypedResults.Ok();
        }

        if (!RateLimiter.IsAllowed($"playfab_setobjects:{playerId}", 30, TimeSpan.FromMinutes(1)))
        {
            Log.Debug($"Player {playerId} hit the set objects rate limit");
            return TypedResults.BadRequest();
        }

        try
        {
            PersonaProfiles profiles = await GetStoredProfilesAsync(playerId);

            foreach (SetObjectsRequest.SetObjectR obj in request.Objects)
            {
                if (!string.IsNullOrEmpty(obj.ObjectName) && obj.DataObject is not null)
                {
                    // equip-side is PERMISSIVE: whatever the client got (through
                    // the offer, an earlier leak or its local cache) it may
                    // equip — the profile is stored verbatim. All content
                    // policy (pack gating, only-supported, category caps)
                    // lives in the provisioning paths only: Catalog/Search,
                    // Catalog/GetItem and the persona binary endpoint.
                    profiles.Objects[obj.ObjectName] = obj.DataObject.Value.Clone();
                }
            }

            await new EarthDB.Query(true)
                .Update("personaProfile", playerId, profiles)
                .ExecuteAsync(Program.DB, Request.HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            // database errors and anything else that escapes the flow below —
            // a KeyNotFound on a first-time profile row used to surface as a
            // 500 here, which the client showed as a failed profile save
            Log.Error(ex, "SetObjects: storing the persona profile of {PlayerId} failed", playerId);
            return TypedResults.BadRequest();
        }

        return TypedResults.Ok();
    }

    private static async Task<PersonaProfiles> GetStoredProfilesAsync(string playerId)
    {
        EarthDB.Results results = await new EarthDB.Query(false)
            .Get("personaProfile", playerId, typeof(PersonaProfiles))
            .ExecuteAsync(Program.DB);

        // first-time players have no stored row — Results.Get throws
        // KeyNotFoundException on a missing name, so fall back to an empty
        // profile instead of letting the read path 500
        try
        {
            return results.Get<PersonaProfiles>("personaProfile");
        }
        catch (KeyNotFoundException)
        {
            return new PersonaProfiles();
        }
    }

    // The objects as stored by the client, with any player modifications
    // overlaid on the built-in defaults.
    private static async Task<Dictionary<string, object>> GetPersonaObjectsAsync(string playerId)
    {
        Dictionary<string, object> objects = DefaultPersonaObjects();

        PersonaProfiles profiles = await GetStoredProfilesAsync(playerId);
        foreach (KeyValuePair<string, JsonElement> entry in profiles.Objects)
        {
            objects[entry.Key] = new Dictionary<string, object>
            {
                ["ObjectName"] = entry.Key,
                ["DataObject"] = entry.Value,
            };
        }

        return objects;
    }

    // Built-in persona profile objects, used for players who have never
    // changed their skin. Shape matches what the vanilla client expects.
    private static Dictionary<string, object> DefaultPersonaObjects()
    {
        return new Dictionary<string, object>()
        {
            ["personaProfile"] = new Dictionary<string, object>
            {
                ["ObjectName"] = "personaProfile",
                ["DataObject"] = new Dictionary<string, object>
                {
                    ["personaCollection"] = new Dictionary<string, object>
                    {
                        ["universalApp"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["id"] = "00000000-0000-0000-0000-000000000000_",
                                ["isPlatformLocked"] = false,
                                ["isTitleLocked"] = false,
                                ["lastUsedPersonaSlot"] = "persona_profile_persona2",
                                ["packId"] = "00000000-0000-0000-0000-000000000000",
                                ["typeId"] = "skin"
                            },
                        },
                    },
                    ["version"] = "0.0.1",
                }
            },
            ["personaProfile2"] = new Dictionary<string, object>
            {
                ["ObjectName"] = "personaProfile2",
                ["DataObject"] = new Dictionary<string, object>
                {
                    ["slotname"] = "",
                    ["personaCollection"] = new Dictionary<string, object?>
                    {
                        ["universalApp"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["arm"] = "wide",
                                ["skcol"] = "#ffb37b62",
                                ["skin"] = false,
                            },
                            new Dictionary<string, object> { ["id"] = "c18e65aa-7b21-4637-9b63-8ad63622ef01.Custome4e3c9cbcb5b4b179a1034a6d5ef3d29" ,},
                            new Dictionary<string, object> { ["id"] = "8f96d1f8-e9bb-40d2-acc8-eb79746c5d7c/d" ,},
                            new Dictionary<string, object> { ["id"] = "1042557f-d1f9-44e3-ba78-f404e8fb7363/d" ,},
                            new Dictionary<string, object> { ["id"] = "f1e4c577-19ba-4d77-9222-47f145857f78/d" ,},
                            new Dictionary<string, object> { ["id"] = "49f93789-a512-4c47-95cb-0606cdc1c2be/d", },
                            new Dictionary<string, object> { ["id"] = "68bfe60d-f30a-422f-b32e-72374ebdd057/d", },
                            new Dictionary<string, object> { ["id"] = "b6702f0e-a4b5-497a-8820-6c8e3946bb55/d", },
                            new Dictionary<string, object>
                            {
                                ["col"] = DataArray0,
                                ["id"] = "a0f263b3-e093-4c85-aadb-3759417898ff/d",
                            },
                            new Dictionary<string, object>
                            {
                                ["col"] = DataArray1,
                                ["id"] = "2bb1473b-7a5c-4eae-9fd5-82302a6aa3da/d",
                            },
                            new Dictionary<string, object>
                            {
                                ["col"] = DataArray2,
                                ["id"] = "2e4341c3-c014-4114-94b5-73968230aaaf/d",
                            },
                        },
                        ["tplApps"] = null,
                        ["tlApps"] = null,
                        ["plApps"] = null,
                    },
                    ["version"] = "0.0.1",
                }
            },
            ["personaProfile3"] = new Dictionary<string, object>
            {
                ["ObjectName"] = "personaProfile3",
                ["DataObject"] = new Dictionary<string, object>
                {
                    ["personaCollection"] = new Dictionary<string, object>
                    {
                        ["universalApp"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["arm"] = "wide",
                                ["skcol"] = "#ffb37b62",
                                ["skin"] = false,
                            },
                            new Dictionary<string, object> { ["id"] = "", },
                            new Dictionary<string, object> { ["id"] = "8f96d1f8-e9bb-40d2-acc8-eb79746c5d7c/d", },
                            new Dictionary<string, object> { ["id"] = "1042557f-d1f9-44e3-ba78-f404e8fb7363/d", },
                            new Dictionary<string, object> { ["id"] = "f1e4c577-19ba-4d77-9222-47f145857f78/d", },
                            new Dictionary<string, object> { ["id"] = "49f93789-a512-4c47-95cb-0606cdc1c2be/d", },
                            new Dictionary<string, object> { ["id"] = "68bfe60d-f30a-422f-b32e-72374ebdd057/d" ,},
                            new Dictionary<string, object> { ["id"] = "b6702f0e-a4b5-497a-8820-6c8e3946bb55/d", },
                            new Dictionary<string, object>
                            {
                                ["col"] = new object[] { "#0", "#0", "#ff774235", "#0" },
                                ["id"] = "52dd0726-cd68-4d7d-8561-515a4866de39/d",
                            },
                            new Dictionary<string, object>
                            {
                                ["col"] = new object[] { "#ff523d89", "#0", "#0", "#0" },
                                ["id"] = "a0f263b3-e093-4c85-aadb-3759417898ff/d",
                            },
                            new Dictionary<string, object>
                            {
                                ["col"] = new object[] { "#ff2f1f0f", "#0", "#0", "#0" },
                                ["id"] = "2bb1473b-7a5c-4eae-9fd5-82302a6aa3da/d",
                            }
                        }
                    },
                    ["version"] = "0.0.1",
                }
            },
            ["personaProfile4"] = new Dictionary<string, object>
            {
                ["ObjectName"] = "personaProfile4",
                ["DataObject"] = new Dictionary<string, object>
                {
                    ["personaCollection"] = new Dictionary<string, object>
                    {
                        ["universalApp"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["arm"] = "slim",
                                ["skcol"] = "#fff2dbbd",
                                ["skin"] = true,
                            },
                            new Dictionary<string, object> { ["id"] = "8f96d1f8-e9bb-40d2-acc8-eb79746c5d7c/d", },
                            new Dictionary<string, object> { ["id"] = "1042557f-d1f9-44e3-ba78-f404e8fb7363/d", },
                            new Dictionary<string, object> { ["id"] = "0948e089-6f9c-40c1-886b-cd37add03f69/d", },
                            new Dictionary<string, object> { ["id"] = "96db6e5b-dc69-4ebc-bd36-cb1b08ffb0f4/d", },
                            new Dictionary<string, object> { ["id"] = "5f64b737-b88a-40ea-be1f-559840237146/d" ,},
                            new Dictionary<string, object>
                            {
                                ["col"] = new object[] { "#0", "#0", "#ffefbbb1", "#0" },
                                ["id"] = "83c940ce-d7b8-4603-8d73-c1234e322cce/d",
                            },
                            new Dictionary<string, object>
                            {
                                ["col"] = new object[] { "#ff236224", "#0", "#0", "#0" },
                                ["id"] = "a0f263b3-e093-4c85-aadb-3759417898ff/d",
                            },
                            new Dictionary<string, object>
                            {
                                ["col"] = new object[] { "#ffe89d4c", "#0", "#0", "#0" },
                                ["id"] = "70be0801-a93f-4ce0-8e3f-7fdeac1e03b9/d",
                            },
                            new Dictionary<string, object> { ["id"] = "80eda582-cda7-4fce-9d6f-89a60f2448f1/d" }
                        }
                    },
                    ["version"] = "0.0.1",
                }
            },
        };
    }
}
