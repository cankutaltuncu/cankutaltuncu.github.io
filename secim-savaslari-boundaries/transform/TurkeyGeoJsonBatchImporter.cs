using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GoldenGamess.SelectionWars.Data;
using GoldenGamess.SelectionWars.Gameplay;
using UnityEditor;
using UnityEngine;

namespace GoldenGamess.SelectionWars.Editor
{
    public static class TurkeyGeoJsonBatchImporter
    {
        private const string Adm1Path = "Assets/_Project/DataSources/Maps/geoBoundaries-TUR-ADM1_simplified.json";
        private const string Adm2Path = "Assets/_Project/DataSources/Maps/geoBoundaries-TUR-ADM2_simplified.json";
        private const string GeneratedProvinceRoot = "Assets/_Project/Data/Provinces/Generated";
        private const string GeneratedDistrictRoot = "Assets/_Project/Data/Districts/Generated";
        private const string LocalizationPath = "Assets/_Project/Resources/SelectionWars/TR.asset";
        private const string MapDatabasePath = "Assets/_Project/Resources/SelectionWars/ProvinceMapDatabase.asset";
        private const string ReportPath = "Assets/_Project/Documentation/ValidationReports/turkey-map-import.txt";
        private const float SimplificationToleranceDegrees = 0.0015f;
        private const float AdjacencyToleranceDegrees = 0.00035f;
        private const float CoordinateScale = 3f;

        [MenuItem("Tools/Selection Wars/Import Turkey ADM1 + ADM2")]
        public static void ImportFromMenu()
        {
            if (!EditorUtility.DisplayDialog(
                    "Türkiye harita verisini içe aktar",
                    "Sabit geoBoundaries ADM1/ADM2 kaynakları okunacak ve yalnızca Generated harita klasörleri yeniden üretilecek.",
                    "İçe aktar",
                    "İptal")) return;

            ImportDefaultSources();
        }

        public static void ImportDefaultSources()
        {
            DateTime started = DateTime.UtcNow;
            if (!File.Exists(Adm1Path)) throw new FileNotFoundException("ADM1 GeoJSON bulunamadı", Adm1Path);
            if (!File.Exists(Adm2Path)) throw new FileNotFoundException("ADM2 GeoJSON bulunamadı", Adm2Path);

            List<GeoFeature> provinces = ParseFeatureCollection(File.ReadAllText(Adm1Path, Encoding.UTF8), true);
            List<GeoFeature> districts = ParseFeatureCollection(File.ReadAllText(Adm2Path, Encoding.UTF8), false);
            if (provinces.Count != 81) throw new InvalidDataException("ADM1 dosyasında 81 il bekleniyordu; bulunan: " + provinces.Count);
            if (districts.Count != 973) throw new InvalidDataException("Sabit ADM2 dosyasında 973 ilçe bekleniyordu; bulunan: " + districts.Count);

            Dictionary<int, ProvinceImport> byPlate = BuildProvinceIndex(provinces);
            int fallbackAssignments = AssignDistricts(districts, byPlate);
            ValidateAssignments(byPlate, districts.Count);

            int syntheticGameplayLinks = 0;
            foreach (ProvinceImport province in byPlate.Values)
            {
                BuildAdjacency(province.districts);
                syntheticGameplayLinks += EnsureConnectedGraph(province.districts);
            }

            RecreateGeneratedFolders();
            LocalizationTable localization = LoadOrCreateLocalization();
            Dictionary<string, string> localizedNames = BuildLocalizationIndex(localization);
            StringBuilder report = new StringBuilder();
            report.AppendLine("SELECTION WARS TURKEY GEOJSON IMPORT");
            report.AppendLine("generatedUtc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            report.AppendLine("adm1=" + Adm1Path);
            report.AppendLine("adm1Sha256=" + Sha256(Adm1Path));
            report.AppendLine("adm2=" + Adm2Path);
            report.AppendLine("adm2Sha256=" + Sha256(Adm2Path));
            report.AppendLine("simplificationToleranceDegrees=" + SimplificationToleranceDegrees.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("coordinateScale=" + CoordinateScale.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("fallbackAssignments=" + fallbackAssignments);

            int generatedDistricts = 0;
            int adjacencyLinks = 0;
            int holes = 0;
            List<ProvinceData> generatedProvinces = new List<ProvinceData>(81);
            for (int plate = 1; plate <= 81; plate++)
            {
                ProvinceImport province = byPlate[plate];
                ProvinceData provinceAsset = CreateProvinceAsset(province, localizedNames, ref generatedDistricts, ref adjacencyLinks, ref holes);
                AssetDatabase.CreateAsset(provinceAsset, GeneratedProvinceRoot + "/" + plate.ToString("00") + "-" + Slug(province.feature.name) + ".asset");
                generatedProvinces.Add(provinceAsset);
                report.AppendLine($"province={plate:00}|name={province.feature.name}|districts={province.districts.Count}");
            }

            SaveMapDatabase(generatedProvinces);
            localizedNames["map.placeholder_notice"] = "Gerçek il ve ilçe sınırları hazır; görsel harita bağlantısı geliştiriliyor.";
            ApplyLocalization(localization, localizedNames);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            report.AppendLine($"SUMMARY provinces={byPlate.Count}, districts={generatedDistricts}, undirectedAdjacencyLinks={adjacencyLinks / 2}, syntheticGameplayLinks={syntheticGameplayLinks}, holes={holes}");
            report.AppendLine("elapsedSeconds=" + (DateTime.UtcNow - started).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, report.ToString(), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(ReportPath);
            Debug.Log($"[Selection Wars] Gerçek Türkiye haritası içe aktarıldı: {byPlate.Count} il, {generatedDistricts} ilçe, {adjacencyLinks / 2} komşuluk ({syntheticGameplayLinks} oynanış bağlantısı). Rapor: {ReportPath}");
        }

        private static Dictionary<int, ProvinceImport> BuildProvinceIndex(List<GeoFeature> provinces)
        {
            Dictionary<int, ProvinceImport> result = new Dictionary<int, ProvinceImport>();
            for (int i = 0; i < provinces.Count; i++)
            {
                GeoFeature feature = provinces[i];
                int plate = PlateFromIso(feature.iso);
                if (plate < 1 || plate > 81) throw new InvalidDataException("Geçersiz il ISO/plaka kodu: " + feature.iso + " (" + feature.name + ")");
                if (result.ContainsKey(plate)) throw new InvalidDataException("Tekrarlanan plaka kodu: " + plate);
                result.Add(plate, new ProvinceImport { plateCode = plate, feature = feature });
            }
            return result;
        }

        private static int AssignDistricts(List<GeoFeature> districts, Dictionary<int, ProvinceImport> provinces)
        {
            int fallbackCount = 0;
            for (int i = 0; i < districts.Count; i++)
            {
                GeoFeature district = districts[i];
                Vector2 point = InteriorPoint(district);
                ProvinceImport match = null;
                foreach (ProvinceImport province in provinces.Values)
                {
                    if (!province.feature.bounds.Contains(point, AdjacencyToleranceDegrees)) continue;
                    if (!Contains(province.feature, point)) continue;
                    match = province;
                    break;
                }

                if (match == null)
                {
                    fallbackCount++;
                    int bestScore = -1;
                    foreach (ProvinceImport province in provinces.Values)
                    {
                        if (!province.feature.bounds.Intersects(district.bounds, 0.01f)) continue;
                        int score = ContainmentScore(province.feature, district);
                        if (score <= bestScore) continue;
                        bestScore = score;
                        match = province;
                    }
                }

                if (match == null) throw new InvalidDataException("İle atanamayan ilçe: " + district.name + " / " + district.id);
                match.districts.Add(district);
            }
            return fallbackCount;
        }

        private static int ContainmentScore(GeoFeature province, GeoFeature district)
        {
            int score = 0;
            for (int p = 0; p < district.parts.Count; p++)
            {
                List<Vector2> ring = district.parts[p].outer;
                int stride = Mathf.Max(1, ring.Count / 24);
                for (int i = 0; i < ring.Count; i += stride)
                    if (Contains(province, ring[i])) score++;
            }
            if (Contains(province, InteriorPoint(district))) score += 100;
            return score;
        }

        private static void ValidateAssignments(Dictionary<int, ProvinceImport> provinces, int expectedDistricts)
        {
            int total = 0;
            for (int plate = 1; plate <= 81; plate++)
            {
                if (!provinces.TryGetValue(plate, out ProvinceImport province)) throw new InvalidDataException("Eksik plaka: " + plate);
                if (province.districts.Count == 0) throw new InvalidDataException("İlçesiz il: " + province.feature.name);
                total += province.districts.Count;
            }
            if (total != expectedDistricts) throw new InvalidDataException($"İlçe atama toplamı hatalı: {total}/{expectedDistricts}");
        }

        private static ProvinceData CreateProvinceAsset(ProvinceImport source, Dictionary<string, string> localizedNames, ref int districtCount, ref int adjacencyLinks, ref int holes)
        {
            int plate = source.plateCode;
            string provinceKey = "province." + plate.ToString("00");
            localizedNames[provinceKey] = source.feature.name;
            ProvinceData province = ScriptableObject.CreateInstance<ProvinceData>();
            province.name = plate.ToString("00") + " " + source.feature.name;
            province.plateCode = plate;
            province.provinceId = "tr-" + plate.ToString("00");
            province.provinceNameKey = provinceKey;
            province.isFictionalTestData = false;
            province.cameraCenter = Vector2.zero;
            float worldWidth = source.feature.bounds.Width * CoordinateScale;
            float worldHeight = source.feature.bounds.Height * CoordinateScale;
            float portraitAspect = 9f / 16f;
            float verticalFit = worldHeight * 0.58f;
            float horizontalFit = worldWidth / (2f * portraitAspect) * 1.08f;
            province.cameraZoom = Mathf.Max(2.75f, Mathf.Max(verticalFit, horizontalFit));
            province.districts = new List<DistrictData>();

            source.districts.Sort((a, b) => StringComparer.Create(new CultureInfo("tr-TR"), true).Compare(a.name, b.name));
            HashSet<string> usedIds = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<GeoFeature, string> districtIds = new Dictionary<GeoFeature, string>();
            for (int i = 0; i < source.districts.Count; i++)
            {
                GeoFeature imported = source.districts[i];
                string baseId = "tr-" + plate.ToString("00") + "-" + Slug(imported.name);
                string districtId = baseId;
                if (!usedIds.Add(districtId))
                {
                    districtId = baseId + "-" + ShortId(imported.id);
                    if (!usedIds.Add(districtId)) throw new InvalidDataException("Tekrarlanan ilçe kimliği: " + districtId);
                }
                districtIds.Add(imported, districtId);
            }

            string districtFolder = GeneratedDistrictRoot + "/" + plate.ToString("00");
            EnsureAssetFolder(districtFolder);
            Vector2 origin = source.feature.bounds.Center;

            for (int i = 0; i < source.districts.Count; i++)
            {
                GeoFeature imported = source.districts[i];
                string districtId = districtIds[imported];

                DistrictData district = ScriptableObject.CreateInstance<DistrictData>();
                district.name = imported.name;
                district.districtId = districtId;
                district.districtNameKey = "district." + districtId;
                localizedNames[district.districtNameKey] = imported.name;
                district.neighboringDistrictIds = new List<string>();
                for (int n = 0; n < imported.neighbors.Count; n++)
                {
                    GeoFeature neighbor = imported.neighbors[n];
                    district.neighboringDistrictIds.Add(districtIds[neighbor]);
                }
                adjacencyLinks += district.neighboringDistrictIds.Count;

                district.polygonParts = NormalizeParts(imported.parts, origin, ref holes);
                district.supportsMultiplePolygons = district.polygonParts.Count > 1;
                PolygonPart primary = LargestPart(district.polygonParts);
                district.polygon = new List<Vector2>(primary.outer);
                district.labelPosition = SafeInteriorPoint(primary);
                district.transferOrigin = district.labelPosition;
                string assetPath = districtFolder + "/" + districtId + ".asset";
                AssetDatabase.CreateAsset(district, assetPath);
                province.districts.Add(district);
                districtCount++;
            }
            return province;
        }

        private static List<PolygonPart> NormalizeParts(List<GeoPart> source, Vector2 origin, ref int holeCount)
        {
            List<PolygonPart> result = new List<PolygonPart>();
            for (int p = 0; p < source.Count; p++)
            {
                PolygonPart part = new PolygonPart();
                part.outer = NormalizeRing(source[p].outer, origin);
                if (part.outer.Count < 3) continue;
                for (int h = 0; h < source[p].holes.Count; h++)
                {
                    List<Vector2> points = NormalizeRing(source[p].holes[h], origin);
                    if (points.Count < 3) continue;
                    part.holes.Add(new Vector2Ring { points = points });
                    holeCount++;
                }
                result.Add(part);
            }
            if (result.Count == 0) throw new InvalidDataException("Sadeleştirme sonrasında geçerli polygon kalmadı.");
            return result;
        }

        private static List<Vector2> NormalizeRing(List<Vector2> source, Vector2 origin)
        {
            List<Vector2> simplified = DouglasPeuckerClosed(source, SimplificationToleranceDegrees);
            List<Vector2> result = new List<Vector2>(simplified.Count);
            for (int i = 0; i < simplified.Count; i++) result.Add((simplified[i] - origin) * CoordinateScale);
            return result;
        }

        private static void BuildAdjacency(List<GeoFeature> districts)
        {
            for (int a = 0; a < districts.Count; a++)
            for (int b = a + 1; b < districts.Count; b++)
            {
                if (!districts[a].bounds.Intersects(districts[b].bounds, AdjacencyToleranceDegrees)) continue;
                if (!ShareBoundary(districts[a], districts[b])) continue;
                districts[a].neighbors.Add(districts[b]);
                districts[b].neighbors.Add(districts[a]);
            }
        }

        private static int EnsureConnectedGraph(List<GeoFeature> districts)
        {
            if (districts.Count < 2) return 0;
            int linksAdded = 0;
            HashSet<GeoFeature> connected = new HashSet<GeoFeature>();
            AddReachable(districts[0], connected);
            while (connected.Count < districts.Count)
            {
                GeoFeature nearestConnected = null;
                GeoFeature nearestOutside = null;
                float nearestDistance = float.MaxValue;
                foreach (GeoFeature a in connected)
                {
                    Vector2 pointA = InteriorPoint(a);
                    for (int i = 0; i < districts.Count; i++)
                    {
                        GeoFeature b = districts[i];
                        if (connected.Contains(b)) continue;
                        float distance = (pointA - InteriorPoint(b)).sqrMagnitude;
                        if (distance >= nearestDistance) continue;
                        nearestDistance = distance;
                        nearestConnected = a;
                        nearestOutside = b;
                    }
                }

                if (nearestConnected == null || nearestOutside == null)
                    throw new InvalidDataException("İlçe komşuluk grafiği bağlanamadı.");
                nearestConnected.neighbors.Add(nearestOutside);
                nearestOutside.neighbors.Add(nearestConnected);
                linksAdded++;
                AddReachable(nearestOutside, connected);
            }
            return linksAdded;
        }

        private static void AddReachable(GeoFeature start, HashSet<GeoFeature> visited)
        {
            if (!visited.Add(start)) return;
            for (int i = 0; i < start.neighbors.Count; i++) AddReachable(start.neighbors[i], visited);
        }

        private static bool ShareBoundary(GeoFeature a, GeoFeature b)
        {
            float toleranceSquared = AdjacencyToleranceDegrees * AdjacencyToleranceDegrees;
            float separationSquared = toleranceSquared * 16f;
            for (int pa = 0; pa < a.parts.Count; pa++)
            for (int pb = 0; pb < b.parts.Count; pb++)
            {
                List<Vector2> ringA = a.parts[pa].outer;
                List<Vector2> ringB = b.parts[pb].outer;
                if (!Bounds2D.From(ringA).Intersects(Bounds2D.From(ringB), AdjacencyToleranceDegrees)) continue;
                if (TwoSeparatedPointsTouchBoundary(ringA, ringB, toleranceSquared, separationSquared)) return true;
                if (TwoSeparatedPointsTouchBoundary(ringB, ringA, toleranceSquared, separationSquared)) return true;
            }
            return false;
        }

        private static bool TwoSeparatedPointsTouchBoundary(List<Vector2> points, List<Vector2> boundary, float toleranceSquared, float separationSquared)
        {
            bool hasFirst = false;
            Vector2 first = Vector2.zero;
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 point = points[i];
                bool touches = false;
                for (int s = 0; s < boundary.Count; s++)
                {
                    if (SegmentDistanceSquared(point, boundary[s], boundary[(s + 1) % boundary.Count]) > toleranceSquared) continue;
                    touches = true;
                    break;
                }
                if (!touches) continue;
                if (!hasFirst) { hasFirst = true; first = point; }
                else if ((point - first).sqrMagnitude > separationSquared) return true;
            }
            return false;
        }

        private static float SegmentDistanceSquared(Vector2 point, Vector2 start, Vector2 end)
        {
            Vector2 segment = end - start;
            float t = segment.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(point - start, segment) / segment.sqrMagnitude) : 0f;
            return (point - (start + segment * t)).sqrMagnitude;
        }

        private static List<GeoFeature> ParseFeatureCollection(string json, bool requireIso)
        {
            Dictionary<string, object> root = AsObject(SimpleJsonParser.Parse(json));
            if (!string.Equals(GetString(root, "type"), "FeatureCollection", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("GeoJSON kökü FeatureCollection olmalı.");
            List<object> values = AsArray(root["features"]);
            List<GeoFeature> result = new List<GeoFeature>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                Dictionary<string, object> featureValue = AsObject(values[i]);
                Dictionary<string, object> properties = AsObject(featureValue["properties"]);
                Dictionary<string, object> geometry = AsObject(featureValue["geometry"]);
                GeoFeature feature = new GeoFeature
                {
                    id = First(properties, "shapeID", "districtId", "id") ?? "feature-" + i,
                    name = First(properties, "shapeName", "districtName", "district", "ilce", "name") ?? "Feature " + i,
                    iso = First(properties, "shapeISO", "iso") ?? string.Empty
                };
                if (requireIso && string.IsNullOrWhiteSpace(feature.iso)) throw new FormatException("İl ISO kodu eksik: " + feature.name);
                ParseGeometry(geometry, feature.parts);
                feature.RecalculateBounds();
                result.Add(feature);
            }
            return result;
        }

        private static void ParseGeometry(Dictionary<string, object> geometry, List<GeoPart> parts)
        {
            string type = GetString(geometry, "type");
            List<object> coordinates = AsArray(geometry["coordinates"]);
            if (string.Equals(type, "Polygon", StringComparison.OrdinalIgnoreCase)) parts.Add(ParsePolygon(coordinates));
            else if (string.Equals(type, "MultiPolygon", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < coordinates.Count; i++) parts.Add(ParsePolygon(AsArray(coordinates[i])));
            }
            else throw new FormatException("Desteklenmeyen geometry.type: " + type);
        }

        private static GeoPart ParsePolygon(List<object> rings)
        {
            if (rings.Count == 0) throw new FormatException("Boş polygon.");
            GeoPart part = new GeoPart { outer = ReadRing(AsArray(rings[0])) };
            for (int i = 1; i < rings.Count; i++) part.holes.Add(ReadRing(AsArray(rings[i])));
            return part;
        }

        private static List<Vector2> ReadRing(List<object> values)
        {
            List<Vector2> result = new List<Vector2>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                List<object> pair = AsArray(values[i]);
                if (pair.Count < 2) continue;
                Vector2 point = new Vector2(Convert.ToSingle(pair[0], CultureInfo.InvariantCulture), Convert.ToSingle(pair[1], CultureInfo.InvariantCulture));
                if (result.Count == 0 || (result[result.Count - 1] - point).sqrMagnitude > 0.000000000001f) result.Add(point);
            }
            if (result.Count > 2 && (result[0] - result[result.Count - 1]).sqrMagnitude < 0.000000000001f) result.RemoveAt(result.Count - 1);
            if (result.Count < 3) throw new FormatException("Üçten az benzersiz noktaya sahip polygon.");
            return result;
        }

        private static Vector2 InteriorPoint(GeoFeature feature)
        {
            GeoPart largest = feature.parts[0];
            float largestArea = Mathf.Abs(SignedArea(largest.outer));
            for (int i = 1; i < feature.parts.Count; i++)
            {
                float area = Mathf.Abs(SignedArea(feature.parts[i].outer));
                if (area <= largestArea) continue;
                largestArea = area;
                largest = feature.parts[i];
            }

            Vector2 centroid = Centroid(largest.outer);
            if (Contains(largest, centroid)) return centroid;
            int[] triangles = PolygonTriangulator.Triangulate(largest.outer);
            float bestArea = -1f;
            Vector2 best = largest.outer[0];
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                Vector2 a = largest.outer[triangles[i]];
                Vector2 b = largest.outer[triangles[i + 1]];
                Vector2 c = largest.outer[triangles[i + 2]];
                Vector2 candidate = (a + b + c) / 3f;
                float area = Mathf.Abs(Cross(b - a, c - a));
                if (area <= bestArea || !Contains(largest, candidate)) continue;
                bestArea = area;
                best = candidate;
            }
            return best;
        }

        private static Vector2 SafeInteriorPoint(PolygonPart part)
        {
            Vector2 centroid = Centroid(part.outer);
            if (Contains(part.outer, centroid)) return centroid;
            int[] triangles = PolygonTriangulator.Triangulate(part.outer);
            if (triangles.Length >= 3) return (part.outer[triangles[0]] + part.outer[triangles[1]] + part.outer[triangles[2]]) / 3f;
            return part.outer[0];
        }

        private static bool Contains(GeoFeature feature, Vector2 point)
        {
            for (int i = 0; i < feature.parts.Count; i++) if (Contains(feature.parts[i], point)) return true;
            return false;
        }

        private static bool Contains(GeoPart part, Vector2 point)
        {
            if (!Contains(part.outer, point)) return false;
            for (int i = 0; i < part.holes.Count; i++) if (Contains(part.holes[i], point)) return false;
            return true;
        }

        private static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 a = polygon[i], b = polygon[j];
                if (SegmentDistanceSquared(point, a, b) < 0.000000000001f) return true;
                if ((a.y > point.y) != (b.y > point.y) && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        private static PolygonPart LargestPart(List<PolygonPart> parts)
        {
            PolygonPart largest = parts[0];
            float largestArea = Mathf.Abs(SignedArea(largest.outer));
            for (int i = 1; i < parts.Count; i++)
            {
                float area = Mathf.Abs(SignedArea(parts[i].outer));
                if (area <= largestArea) continue;
                largestArea = area;
                largest = parts[i];
            }
            return largest;
        }

        private static float SignedArea(IReadOnlyList<Vector2> points)
        {
            float area = 0f;
            for (int i = 0; i < points.Count; i++) area += Cross(points[i], points[(i + 1) % points.Count]);
            return area * 0.5f;
        }

        private static Vector2 Centroid(IReadOnlyList<Vector2> points)
        {
            float areaTwice = 0f;
            float x = 0f;
            float y = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 a = points[i];
                Vector2 b = points[(i + 1) % points.Count];
                float cross = Cross(a, b);
                areaTwice += cross;
                x += (a.x + b.x) * cross;
                y += (a.y + b.y) * cross;
            }
            if (Mathf.Abs(areaTwice) < 0.0000001f) return points[0];
            return new Vector2(x / (3f * areaTwice), y / (3f * areaTwice));
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static List<Vector2> DouglasPeuckerClosed(List<Vector2> points, float tolerance)
        {
            if (points.Count < 5 || tolerance <= 0f) return new List<Vector2>(points);
            int anchor = 0;
            float farthest = -1f;
            for (int i = 1; i < points.Count; i++)
            {
                float distance = (points[i] - points[0]).sqrMagnitude;
                if (distance <= farthest) continue;
                farthest = distance;
                anchor = i;
            }

            List<Vector2> first = new List<Vector2>();
            for (int i = 0; i <= anchor; i++) first.Add(points[i]);
            List<Vector2> second = new List<Vector2>();
            for (int i = anchor; i < points.Count; i++) second.Add(points[i]);
            second.Add(points[0]);
            first = DouglasPeuckerOpen(first, tolerance);
            second = DouglasPeuckerOpen(second, tolerance);
            List<Vector2> result = new List<Vector2>(first.Count + second.Count - 2);
            result.AddRange(first);
            for (int i = 1; i < second.Count - 1; i++) result.Add(second[i]);
            return result.Count >= 3 ? result : new List<Vector2>(points);
        }

        private static List<Vector2> DouglasPeuckerOpen(List<Vector2> points, float tolerance)
        {
            if (points.Count < 3) return points;
            bool[] keep = new bool[points.Count];
            keep[0] = keep[points.Count - 1] = true;
            Simplify(points, 0, points.Count - 1, tolerance * tolerance, keep);
            List<Vector2> result = new List<Vector2>();
            for (int i = 0; i < points.Count; i++) if (keep[i]) result.Add(points[i]);
            return result;
        }

        private static void Simplify(List<Vector2> points, int first, int last, float toleranceSquared, bool[] keep)
        {
            float maxDistance = 0f;
            int best = -1;
            for (int i = first + 1; i < last; i++)
            {
                float distance = SegmentDistanceSquared(points[i], points[first], points[last]);
                if (distance <= maxDistance) continue;
                maxDistance = distance;
                best = i;
            }
            if (best < 0 || maxDistance <= toleranceSquared) return;
            keep[best] = true;
            Simplify(points, first, best, toleranceSquared, keep);
            Simplify(points, best, last, toleranceSquared, keep);
        }

        private static void RecreateGeneratedFolders()
        {
            if (AssetDatabase.IsValidFolder(GeneratedProvinceRoot)) AssetDatabase.DeleteAsset(GeneratedProvinceRoot);
            if (AssetDatabase.IsValidFolder(GeneratedDistrictRoot)) AssetDatabase.DeleteAsset(GeneratedDistrictRoot);
            EnsureAssetFolder(GeneratedProvinceRoot);
            EnsureAssetFolder(GeneratedDistrictRoot);
        }

        private static void EnsureAssetFolder(string folder)
        {
            string[] segments = folder.Split('/');
            string current = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }

        private static LocalizationTable LoadOrCreateLocalization()
        {
            LocalizationTable table = AssetDatabase.LoadAssetAtPath<LocalizationTable>(LocalizationPath);
            if (table != null) return table;
            if (AssetDatabase.LoadMainAssetAtPath(LocalizationPath) != null || File.Exists(LocalizationPath)) AssetDatabase.DeleteAsset(LocalizationPath);
            EnsureAssetFolder(Path.GetDirectoryName(LocalizationPath).Replace('\\', '/'));
            table = ScriptableObject.CreateInstance<LocalizationTable>();
            table.languageCode = "tr";
            table.entries = DefaultGameAssets.TurkishLocalizationEntries();
            AssetDatabase.CreateAsset(table, LocalizationPath);
            return table;
        }

        private static void SaveMapDatabase(List<ProvinceData> provinces)
        {
            ProvinceMapDatabase database = AssetDatabase.LoadAssetAtPath<ProvinceMapDatabase>(MapDatabasePath);
            if (database == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(MapDatabasePath) != null || File.Exists(MapDatabasePath)) AssetDatabase.DeleteAsset(MapDatabasePath);
                EnsureAssetFolder(Path.GetDirectoryName(MapDatabasePath).Replace('\\', '/'));
                database = ScriptableObject.CreateInstance<ProvinceMapDatabase>();
                database.name = "ProvinceMapDatabase";
                AssetDatabase.CreateAsset(database, MapDatabasePath);
            }
            database.provinces = new List<ProvinceData>(provinces);
            EditorUtility.SetDirty(database);
        }

        private static Dictionary<string, string> BuildLocalizationIndex(LocalizationTable table)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < table.entries.Count; i++)
            {
                LocalizationEntry entry = table.entries[i];
                if (!string.IsNullOrWhiteSpace(entry.key)) values[entry.key] = entry.value;
            }
            return values;
        }

        private static void ApplyLocalization(LocalizationTable table, Dictionary<string, string> values)
        {
            List<string> keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.Ordinal);
            table.entries = new List<LocalizationEntry>(keys.Count);
            for (int i = 0; i < keys.Count; i++) table.entries.Add(new LocalizationEntry { key = keys[i], value = values[keys[i]] });
            EditorUtility.SetDirty(table);
        }

        private static string Sha256(string assetPath)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(Path.GetFullPath(assetPath)))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder result = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) result.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private static int PlateFromIso(string iso)
        {
            int separator = iso.LastIndexOf('-');
            string value = separator >= 0 ? iso.Substring(separator + 1) : iso;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int plate) ? plate : -1;
        }

        private static string Slug(string value)
        {
            string prepared = (value ?? string.Empty).Replace('ı', 'i').Replace('İ', 'I').Normalize(NormalizationForm.FormD);
            StringBuilder result = new StringBuilder();
            bool hyphen = false;
            for (int i = 0; i < prepared.Length; i++)
            {
                char c = prepared[i];
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                c = char.ToLowerInvariant(c);
                if (char.IsLetterOrDigit(c)) { result.Append(c); hyphen = false; }
                else if (!hyphen && result.Length > 0) { result.Append('-'); hyphen = true; }
            }
            return result.ToString().Trim('-');
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrEmpty(value)) return "id";
            return value.Length <= 8 ? value.ToLowerInvariant() : value.Substring(value.Length - 8).ToLowerInvariant();
        }

        private static Dictionary<string, object> AsObject(object value) => value as Dictionary<string, object> ?? throw new FormatException("JSON object bekleniyordu.");
        private static List<object> AsArray(object value) => value as List<object> ?? throw new FormatException("JSON array bekleniyordu.");
        private static string GetString(Dictionary<string, object> values, string key) => values.TryGetValue(key, out object value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;

        private static string First(Dictionary<string, object> values, params string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
                if (values.TryGetValue(keys[i], out object value) && value != null && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
            return null;
        }

        private sealed class ProvinceImport
        {
            public int plateCode;
            public GeoFeature feature;
            public readonly List<GeoFeature> districts = new List<GeoFeature>();
        }

        private sealed class GeoFeature
        {
            public string id;
            public string name;
            public string iso;
            public readonly List<GeoPart> parts = new List<GeoPart>();
            public readonly List<GeoFeature> neighbors = new List<GeoFeature>();
            public Bounds2D bounds;

            public void RecalculateBounds()
            {
                bounds = new Bounds2D();
                for (int p = 0; p < parts.Count; p++)
                    for (int i = 0; i < parts[p].outer.Count; i++) bounds.Include(parts[p].outer[i]);
            }
        }

        private sealed class GeoPart
        {
            public List<Vector2> outer = new List<Vector2>();
            public readonly List<List<Vector2>> holes = new List<List<Vector2>>();
        }

        private sealed class Bounds2D
        {
            private Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            private Vector2 max = new Vector2(float.MinValue, float.MinValue);
            public Vector2 Center => (min + max) * 0.5f;
            public float Width => max.x - min.x;
            public float Height => max.y - min.y;
            public void Include(Vector2 point) { min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
            public bool Contains(Vector2 point, float tolerance) => point.x >= min.x - tolerance && point.x <= max.x + tolerance && point.y >= min.y - tolerance && point.y <= max.y + tolerance;
            public bool Intersects(Bounds2D other, float tolerance) => min.x <= other.max.x + tolerance && max.x + tolerance >= other.min.x && min.y <= other.max.y + tolerance && max.y + tolerance >= other.min.y;
            public static Bounds2D From(List<Vector2> points)
            {
                Bounds2D result = new Bounds2D();
                for (int i = 0; i < points.Count; i++) result.Include(points[i]);
                return result;
            }
        }
    }
}
