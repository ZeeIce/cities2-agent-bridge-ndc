using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal.Mathematics;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Policies;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Game.UI.Menu;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CitiesIIAgentBridge
{
    // Read-only views that match game panels: the traffic info view and bottleneck markers, the native
    // cell-map layers, a clean camera capture, loan/fees/utilities, and city and district policies.
    // Data sources follow LancerComet/cities-skylines-2-mcp (Apache-2.0).
    public sealed partial class Mod
    {
        // Road.m_TrafficFlowDistance0 + 1 to the road info panel's volume scale (16 * 4 / 24).
        private const float TrafficVolumeScale = 2.666667f;

        private JObject TrafficGetGameBottlenecks(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var ps = w.GetExistingSystemManaged<PrefabSystem>();
            var area = AreaFilter.From(args, 10000); int limit = (int?)args["limit"] ?? 50;
            if (limit < 1 || limit > 500) throw new ArgumentException("limit_must_be_1_to_500");
            // City-wide averages as TrafficFlowSystem.RefreshCityTrafficAverages computes them for the traffic info view.
            float4 flowSum = 0, volumeSum = 0; int cityRoads;
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Road>(), ComponentType.ReadOnly<Aggregated>(), ComponentType.ReadOnly<Edge>() }, None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Native>() } }))
            using (var roads = q.ToComponentDataArray<Road>(Allocator.Temp))
            {
                foreach (var r in roads) { flowSum += NetUtils.GetTrafficFlowSpeed(r) * 100f; volumeSum += (r.m_TrafficFlowDistance0 + r.m_TrafficFlowDistance1) * TrafficVolumeScale; }
                cityRoads = roads.Length;
            }
            float divisor = math.max(1, cityRoads) * 4f;
            // Lanes TrafficBottleneckSystem marks with Bottleneck (the in-world red car icons), grouped by their road or junction.
            var groups = new Dictionary<Entity, (int lanes, float3 sum)>(); int laneCount = 0;
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Bottleneck>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<Owner>() }, None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() } }))
            using (var lanes = q.ToEntityArray(Allocator.Temp))
                foreach (var lane in lanes)
                {
                    var b = em.GetComponentData<Bottleneck>(lane);
                    var p = MathUtils.Position(em.GetComponentData<Curve>(lane).m_Bezier, b.m_Position / 255f);
                    if (!area.Contains(p)) continue;
                    var owner = em.GetComponentData<Owner>(lane).m_Owner;
                    groups.TryGetValue(owner, out var g); groups[owner] = (g.lanes + 1, g.sum + p); ++laneCount;
                }
            var rows = new JArray(groups.OrderByDescending(p => p.Value.lanes).ThenBy(p => p.Key.Index).Take(limit).Select(p =>
            {
                var row = NativeBuild.Id(p.Key); var pos = p.Value.sum / p.Value.lanes;
                row["kind"] = em.HasComponent<Node>(p.Key) ? "junction" : em.HasComponent<Edge>(p.Key) ? "road" : "other";
                row["prefab"] = em.Exists(p.Key) && em.HasComponent<PrefabRef>(p.Key) ? ps.GetPrefabName(em.GetComponentData<PrefabRef>(p.Key).m_Prefab) : null;
                row["position"] = new JObject { ["x"] = Math.Round(pos.x, 1), ["z"] = Math.Round(pos.z, 1) };
                row["lanes"] = p.Value.lanes;
                if (em.HasComponent<Road>(p.Key)) row["flowPercent"] = Math.Round(math.csum(NetUtils.GetTrafficFlowSpeed(em.GetComponentData<Road>(p.Key)) * 100f) * 0.25f);
                return row;
            }));
            return new JObject
            {
                ["cityTrafficFlowPercent"] = Math.Round(math.csum(flowSum) / divisor), ["cityTrafficVolume"] = Math.Round(math.csum(volumeSum) / divisor),
                ["cityRoadsAveraged"] = cityRoads, ["bottleneckLanes"] = laneCount, ["bottleneckLocations"] = groups.Count, ["bottlenecks"] = rows,
                ["note"] = "City flow and volume as the traffic info view shows them (100% = free-flowing). Bottlenecks are the lanes the game itself marks with the red car icon, grouped by road or junction. Values refresh 32 times per in-game day."
            };
        }

        private static readonly string[] mapLayers = { "landValue", "groundPollution", "airPollution", "noisePollution", "groundWater", "groundWaterPollution" };

        private JObject CityGetMapLayer(JObject args)
        {
            var w = RequireCity(); string layer = (string)args["layer"];
            string key = mapLayers.FirstOrDefault(l => string.Equals(l, layer, StringComparison.OrdinalIgnoreCase));
            if (key == null) throw new ArgumentException("layer_required: one of " + string.Join(", ", mapLayers));
            int resolution = (int?)args["resolution"] ?? 64;
            if (resolution < 4 || resolution > 256) throw new ArgumentException("resolution_must_be_4_to_256");
            float[] cells; JobHandle ready;
            switch (key)
            {
                case "landValue": { var m = w.GetExistingSystemManaged<LandValueSystem>().GetMap(true, out ready); ready.Complete(); cells = m.Select(c => c.m_LandValue).ToArray(); break; }
                case "groundPollution": { var m = w.GetExistingSystemManaged<GroundPollutionSystem>().GetMap(true, out ready); ready.Complete(); cells = m.Select(c => (float)c.m_Pollution).ToArray(); break; }
                case "airPollution": { var m = w.GetExistingSystemManaged<AirPollutionSystem>().GetMap(true, out ready); ready.Complete(); cells = m.Select(c => (float)c.m_Pollution).ToArray(); break; }
                case "noisePollution": { var m = w.GetExistingSystemManaged<NoisePollutionSystem>().GetMap(true, out ready); ready.Complete(); cells = m.Select(c => (float)c.m_Pollution).ToArray(); break; }
                case "groundWater": { var m = w.GetExistingSystemManaged<GroundWaterSystem>().GetMap(true, out ready); ready.Complete(); cells = m.Select(c => (float)c.m_Amount).ToArray(); break; }
                default: { var m = w.GetExistingSystemManaged<GroundWaterSystem>().GetMap(true, out ready); ready.Complete(); cells = m.Select(c => (float)c.m_Polluted).ToArray(); break; }
            }
            int size = (int)Math.Round(Math.Sqrt(cells.Length)); float half = CellMapSystem<GroundWater>.kMapSize / 2f, cell = half * 2 / size;
            // Area: bbox, x/z/radius (as its bounding square) or the whole playable map.
            var area = AreaFilter.From(args, 10000);
            float4 box = !area.Active ? new float4(-half, -half, half, half) : area.IsBox ? area.Box : new float4(area.Center.x - area.Radius, area.Center.y - area.Radius, area.Center.x + area.Radius, area.Center.y + area.Radius);
            int c0 = math.clamp((int)math.floor((box.x + half) / cell), 0, size - 1), c1 = math.clamp((int)math.ceil((box.z + half) / cell) - 1, 0, size - 1);
            int r0 = math.clamp((int)math.floor((box.y + half) / cell), 0, size - 1), r1 = math.clamp((int)math.ceil((box.w + half) / cell) - 1, 0, size - 1);
            int cols = c1 - c0 + 1, rows = r1 - r0 + 1, outCols = Math.Min(resolution, cols), outRows = Math.Min(resolution, rows);
            var grid = new JArray(); float min = float.MaxValue, max = float.MinValue; double sum = 0; int n = 0; float peak = float.MinValue; int peakRow = 0, peakCol = 0;
            for (int r = r0; r <= r1; r++) for (int c = c0; c <= c1; c++) { float v = cells[r * size + c]; min = Math.Min(min, v); max = Math.Max(max, v); sum += v; ++n; if (v > peak) { peak = v; peakRow = r; peakCol = c; } }
            for (int orow = 0; orow < outRows; orow++)
            {
                var line = new JArray(); int ra = r0 + orow * rows / outRows, rb = r0 + (orow + 1) * rows / outRows;
                for (int ocol = 0; ocol < outCols; ocol++)
                {
                    int ca = c0 + ocol * cols / outCols, cb = c0 + (ocol + 1) * cols / outCols; double s = 0; int k = 0;
                    for (int r = ra; r < rb; r++) for (int c = ca; c < cb; c++) { s += cells[r * size + c]; ++k; }
                    line.Add(Math.Round(k == 0 ? 0 : s / k, 2));
                }
                grid.Add(line);
            }
            return new JObject
            {
                ["layer"] = key, ["nativeCells"] = size, ["nativeCellMetres"] = Math.Round(cell, 2),
                ["bounds"] = new JObject { ["minX"] = -half + c0 * cell, ["minZ"] = -half + r0 * cell, ["maxX"] = -half + (c1 + 1) * cell, ["maxZ"] = -half + (r1 + 1) * cell },
                ["columns"] = outCols, ["rows"] = outRows, ["outputCellMetres"] = new JObject { ["x"] = Math.Round(cols * cell / outCols, 2), ["z"] = Math.Round(rows * cell / outRows, 2) },
                ["min"] = min, ["max"] = max, ["mean"] = Math.Round(n == 0 ? 0 : sum / n, 3),
                ["peak"] = new JObject { ["value"] = peak, ["x"] = Math.Round(-half + (peakCol + 0.5f) * cell, 1), ["z"] = Math.Round(-half + (peakRow + 0.5f) * cell, 1) },
                ["values"] = grid,
                ["note"] = "values[row][column]: row 0 is minZ, column 0 is minX; each output cell is the mean of the native cells it covers. Latest completed native map, no simulation forced."
            };
        }

        private JObject CityCaptureView(JObject args)
        {
            RequireCity();
            int width = (int?)args["width"] ?? 1280;
            if (width < 160 || width > 3840) throw new ArgumentException("width_must_be_160_to_3840");
            var camera = UnityEngine.Camera.main; if (camera == null) throw new InvalidOperationException("no_main_camera");
            int height = Math.Max(90, (int)Math.Round(width * (double)Screen.height / Math.Max(1, Screen.width)));
            var dir = Path.Combine(mailbox.Root, "captures"); Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "view-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png");
            RenderTexture target = null; Texture2D pixels = null; var previous = RenderTexture.active;
            try
            {
                // The save-preview capture renders the main camera with UI, overlays and outlines hidden.
                target = ScreenCaptureHelper.CreateRenderTarget("CitiesIIAgentBridge-view", width, height);
                ScreenCaptureHelper.CaptureScreenshot(camera, target, new MenuHelpers.SaveGamePreviewSettings { stylized = false });
                RenderTexture.active = target;
                pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0); pixels.Apply();
                var png = ImageConversion.EncodeToPNG(pixels);
                if (png == null || png.Length == 0) throw new InvalidOperationException("png_encode_failed");
                File.WriteAllBytes(path, png);
                return new JObject { ["path"] = path, ["width"] = width, ["height"] = height, ["bytes"] = png.Length, ["camera"] = CameraState(),
                    ["note"] = "Rendered from the main camera without UI, overlays or info views. Use set_camera first to frame an area." };
            }
            finally
            {
                RenderTexture.active = previous;
                if (pixels != null) UnityEngine.Object.Destroy(pixels);
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
            }
        }

        private JObject CityGetFinances()
        {
            var w = RequireCity(); var em = w.EntityManager; var city = w.GetExistingSystemManaged<CitySystem>().City;
            var loans = w.GetExistingSystemManaged<LoanSystem>(); var loan = loans.CurrentLoan;
            var feeSystem = w.GetExistingSystemManaged<ServiceFeeSystem>(); var fees = new JObject();
            if (em.HasBuffer<ServiceFee>(city))
            {
                var buffer = em.GetBuffer<ServiceFee>(city, true);
                foreach (PlayerResource r in Enum.GetValues(typeof(PlayerResource)))
                    if ((int)r >= 0 && ServiceFeeSystem.TryGetFee(r, buffer, out float fee))
                    {
                        var range = feeSystem.GetServiceFees(r);
                        fees[r.ToString()] = new JObject { ["fee"] = fee, ["estimatedIncome"] = feeSystem.GetServiceFeeIncomeEstimate(r, fee), ["sliderMin"] = range.x, ["sliderMax"] = range.y, ["default"] = range.z };
                    }
            }
            return new JObject
            {
                ["loan"] = new JObject { ["amount"] = loan.m_Amount, ["dailyInterestRate"] = loan.m_DailyInterestRate, ["dailyPayment"] = loan.m_DailyPayment, ["creditworthiness"] = loans.Creditworthiness },
                ["serviceFees"] = fees,
                ["note"] = "Taxes and service budgets are in get_city_management. Fee income estimates come from ServiceFeeSystem, as in the economy panel."
            };
        }

        private JObject CityGetUtilities()
        {
            var w = RequireCity();
            var e = w.GetExistingSystemManaged<ElectricityStatisticsSystem>(); var et = w.GetExistingSystemManaged<ElectricityTradeSystem>();
            var water = w.GetExistingSystemManaged<WaterStatisticsSystem>(); var wt = w.GetExistingSystemManaged<WaterTradeSystem>();
            var garbage = w.GetExistingSystemManaged<GarbageAccumulationSystem>();
            return new JObject
            {
                ["electricity"] = new JObject { ["production"] = e.production, ["consumption"] = e.consumption, ["fulfilledConsumption"] = e.fulfilledConsumption,
                    ["batteryCharge"] = e.batteryCharge, ["batteryCapacity"] = e.batteryCapacity, ["import"] = et.import, ["export"] = et.export },
                ["water"] = new JObject { ["freshCapacity"] = water.freshCapacity, ["freshConsumption"] = water.freshConsumption, ["sewageCapacity"] = water.sewageCapacity,
                    ["sewageConsumption"] = water.sewageConsumption, ["freshImport"] = wt.freshImport, ["freshExport"] = wt.freshExport, ["sewageExport"] = wt.sewageExport },
                ["garbage"] = new JObject { ["accumulationRate"] = garbage.garbageAccumulation },
                ["note"] = "Native units, as the electricity, water and garbage info views read them."
            };
        }

        private JObject CityGetPolicies(JObject args)
        {
            var w = RequireCity(); var em = w.EntityManager; var ps = w.GetExistingSystemManaged<PrefabSystem>();
            Entity owner; bool district = args["districtIndex"] != null;
            if (district)
            {
                owner = new Entity { Index = RequiredInt(args, "districtIndex"), Version = RequiredInt(args, "districtVersion") };
                if (!em.Exists(owner) || !em.HasComponent<Game.Areas.District>(owner)) throw new ArgumentException("district_not_found");
            }
            else owner = w.GetExistingSystemManaged<CitySystem>().City;
            var active = new Dictionary<Entity, Policy>();
            if (em.HasBuffer<Policy>(owner)) foreach (var p in em.GetBuffer<Policy>(owner, true)) active[p.m_Policy] = p;
            var any = district ? new[] { ComponentType.ReadOnly<DistrictOptionData>(), ComponentType.ReadOnly<DistrictModifierData>() } : new[] { ComponentType.ReadOnly<CityOptionData>(), ComponentType.ReadOnly<CityModifierData>() };
            var dictionary = GameManager.instance?.localizationManager?.activeDictionary; var rows = new JArray();
            using (var q = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<PolicyData>() }, Any = any }))
            using (var es = q.ToEntityArray(Allocator.Temp))
                foreach (var e in es.ToArray().OrderBy(e => e.Index))
                {
                    var prefab = ps.GetPrefab<PolicyPrefab>(e);
                    if (prefab == null || prefab.m_Visibility == PolicyVisibility.HideFromPolicyList) continue;
                    string title = null; dictionary?.TryGetValue("Policy.TITLE[" + prefab.name + "]", out title);
                    bool on = active.TryGetValue(e, out var state) && (state.m_Flags & PolicyFlags.Active) != 0;
                    rows.Add(new JObject { ["name"] = prefab.name, ["title"] = title, ["active"] = on, ["adjustment"] = on ? state.m_Adjustment : 0f,
                        ["hasSlider"] = em.HasComponent<PolicySliderData>(e), ["locked"] = em.HasComponent<Locked>(e) && em.IsComponentEnabled<Locked>(e) });
                }
            return new JObject { ["scope"] = district ? "district" : "city", ["owner"] = NativeBuild.Id(owner), ["activeCount"] = rows.Count(r => (bool)r["active"]), ["policies"] = rows };
        }
    }
}
