using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Camera_FOV.Services
{
    /// <summary>
    /// Stores plugin ownership tags on Revit elements using Extensible Storage, so the data
    /// lives inside each project (per document) and survives closing the window or Revit.
    ///
    /// Every tag records the UniqueId of the element it was written to. Revit copies
    /// extensible storage along with copied elements, but a copy gets a new UniqueId, so a
    /// copied element is recognised as "not ours" and is never replaced or deleted.
    /// </summary>
    public static class ElementTagStorage
    {
        // Schema GUIDs must never change once released: existing projects reference them.
        private static readonly Guid CoverageSchemaGuid = new Guid("6C0E2F4B-9A51-4D6B-8E0B-3B1F6A2C7D41");
        private static readonly Guid TracedBoundarySchemaGuid = new Guid("B7D3A9E2-4F18-4C3A-9D6E-5A2E8C1F0B63");
        private static readonly Guid CoverageSourceSchemaGuid = new Guid("E4A1C7D9-2B6F-4F3E-8A15-9C0D7B3E6F28");
        private static readonly Guid TracingRulesSchemaGuid = new Guid("5B9E3F14-7C2A-4D81-B6E0-2F8A4C9D1E57");

        private const string FieldRules = "Rules";

        private const string FieldCameraState = "CameraState";
        private const string FieldBoundaryState = "BoundaryState";
        private const string FieldReach = "Reach";

        private const string FieldOwnerUniqueId = "OwnerUniqueId";
        private const string FieldViewUniqueId = "ViewUniqueId";
        private const string FieldCameraUniqueId = "CameraUniqueId";
        private const string FieldRegionTypeUniqueId = "RegionTypeUniqueId";

        #region Camera coverage regions

        /// <summary>
        /// Tags a generated coverage region with its camera, DORI filled region type and owning view.
        /// Must be called inside an open transaction.
        /// </summary>
        public static void TagCoverageRegion(FilledRegion region, Element camera, ElementId regionTypeId, View view)
        {
            if (region == null || camera == null || regionTypeId == null || view == null) return;

            Element regionType = region.Document.GetElement(regionTypeId);
            if (regionType == null) return;

            Schema schema = GetCoverageSchema();
            Entity entity = new Entity(schema);
            entity.Set(FieldOwnerUniqueId, region.UniqueId);
            entity.Set(FieldViewUniqueId, view.UniqueId);
            entity.Set(FieldCameraUniqueId, camera.UniqueId);
            entity.Set(FieldRegionTypeUniqueId, regionType.UniqueId);
            region.SetEntity(entity);
        }

        /// <summary>
        /// Finds regions previously generated for this camera + DORI type in this view.
        /// Untagged regions (drawn before tagging existed) and copies are never returned.
        /// </summary>
        public static List<ElementId> FindCoverageRegions(Document doc, View view, Element camera, ElementId regionTypeId)
        {
            var result = new List<ElementId>();
            if (doc == null || view == null || camera == null || regionTypeId == null) return result;

            Schema schema = Schema.Lookup(CoverageSchemaGuid);
            if (schema == null) return result; // Nothing has ever been tagged in this session/project

            Element regionType = doc.GetElement(regionTypeId);
            if (regionType == null) return result;

            var regions = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(FilledRegion))
                .WhereElementIsNotElementType();

            foreach (Element region in regions)
            {
                Entity entity = region.GetEntity(schema);
                if (!IsOwnedBy(entity, region)) continue;

                if (entity.Get<string>(FieldViewUniqueId) == view.UniqueId
                    && entity.Get<string>(FieldCameraUniqueId) == camera.UniqueId
                    && entity.Get<string>(FieldRegionTypeUniqueId) == regionType.UniqueId)
                {
                    result.Add(region.Id);
                }
            }

            return result;
        }

        /// <summary>
        /// All regions generated for this camera in this view, whatever their DORI type.
        /// </summary>
        public static List<ElementId> FindCameraCoverage(Document doc, View view, Element camera)
        {
            if (camera == null) return new List<ElementId>();

            return FindCoverageByCamera(doc, view).TryGetValue(camera.UniqueId, out List<ElementId> regions)
                ? regions
                : new List<ElementId>();
        }

        /// <summary>
        /// Generated coverage regions in the view, grouped by the UniqueId of their camera.
        /// </summary>
        public static Dictionary<string, List<ElementId>> FindCoverageByCamera(Document doc, View view)
        {
            var result = new Dictionary<string, List<ElementId>>();
            if (doc == null || view == null) return result;

            Schema schema = Schema.Lookup(CoverageSchemaGuid);
            if (schema == null) return result;

            var regions = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(FilledRegion))
                .WhereElementIsNotElementType();

            foreach (Element region in regions)
            {
                Entity entity = region.GetEntity(schema);
                if (!IsOwnedBy(entity, region) || entity.Get<string>(FieldViewUniqueId) != view.UniqueId) continue;

                string cameraId = entity.Get<string>(FieldCameraUniqueId);
                if (!result.TryGetValue(cameraId, out List<ElementId> list))
                    result[cameraId] = list = new List<ElementId>();
                list.Add(region.Id);
            }

            return result;
        }

        /// <summary>
        /// Records what a region was drawn from (see <see cref="CoverageSource"/>).
        /// Must be called inside an open transaction.
        /// </summary>
        public static void TagCoverageSource(Element region, string cameraState, string boundaryState, double reach)
        {
            if (region == null) return;

            Entity entity = new Entity(GetCoverageSourceSchema());
            entity.Set(FieldOwnerUniqueId, region.UniqueId);
            entity.Set(FieldCameraState, cameraState ?? string.Empty);
            entity.Set(FieldBoundaryState, boundaryState ?? string.Empty);
            entity.Set(FieldReach, reach.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            region.SetEntity(entity);
        }

        /// <summary>
        /// Reads what a region was drawn from. False for regions drawn before this was recorded, and for copies.
        /// </summary>
        public static bool TryGetCoverageSource(Element region, out string cameraState, out string boundaryState, out double reach)
        {
            cameraState = boundaryState = null;
            reach = 0;

            Schema schema = Schema.Lookup(CoverageSourceSchemaGuid);
            if (region == null || schema == null) return false;

            Entity entity = region.GetEntity(schema);
            if (!IsOwnedBy(entity, region)) return false;

            cameraState = entity.Get<string>(FieldCameraState);
            boundaryState = entity.Get<string>(FieldBoundaryState);
            double.TryParse(entity.Get<string>(FieldReach), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out reach);
            return true;
        }

        // Stored in the region type field of the coverage schema to mark a camera's field-of-view
        // dimension, so no new schema is needed.
        private const string FovDimensionMarker = "CameraFovDimension";

        /// <summary>
        /// Tags a generated field-of-view dimension with its camera and owning view.
        /// Must be called inside an open transaction.
        /// </summary>
        public static void TagFovDimension(Dimension dimension, Element camera, View view)
        {
            if (dimension == null || camera == null || view == null) return;

            Entity entity = new Entity(GetCoverageSchema());
            entity.Set(FieldOwnerUniqueId, dimension.UniqueId);
            entity.Set(FieldViewUniqueId, view.UniqueId);
            entity.Set(FieldCameraUniqueId, camera.UniqueId);
            entity.Set(FieldRegionTypeUniqueId, FovDimensionMarker);
            dimension.SetEntity(entity);
        }

        /// <summary>
        /// Finds field-of-view dimensions previously generated for this camera in this view.
        /// Dimensions placed by hand and copies are never returned.
        /// </summary>
        public static List<ElementId> FindFovDimensions(Document doc, View view, Element camera)
        {
            var result = new List<ElementId>();
            if (doc == null || view == null || camera == null) return result;

            Schema schema = Schema.Lookup(CoverageSchemaGuid);
            if (schema == null) return result;

            var dimensions = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Dimension))
                .WhereElementIsNotElementType();

            foreach (Element dimension in dimensions)
            {
                Entity entity = dimension.GetEntity(schema);
                if (!IsOwnedBy(entity, dimension)) continue;

                if (entity.Get<string>(FieldViewUniqueId) == view.UniqueId
                    && entity.Get<string>(FieldCameraUniqueId) == camera.UniqueId
                    && entity.Get<string>(FieldRegionTypeUniqueId) == FovDimensionMarker)
                {
                    result.Add(dimension.Id);
                }
            }

            return result;
        }

        #endregion

        #region Tracing rules (per project)

        /// <summary>
        /// The project's tracing rules, or the defaults when none have been saved.
        /// </summary>
        public static Models.TracingRules LoadTracingRules(Document doc)
        {
            Entity entity = FindTracingRulesEntity(doc, out _);
            return entity != null
                ? Models.TracingRules.Parse(entity.Get<string>(FieldRules))
                : Models.TracingRules.Default;
        }

        /// <summary>
        /// Saves the tracing rules in the project, on one DataStorage element owned by the plugin.
        /// Must be called inside an open transaction.
        /// </summary>
        public static void SaveTracingRules(Document doc, Models.TracingRules rules)
        {
            FindTracingRulesEntity(doc, out DataStorage storage);
            if (storage == null)
                storage = DataStorage.Create(doc);

            Entity entity = new Entity(GetTracingRulesSchema());
            entity.Set(FieldOwnerUniqueId, storage.UniqueId);
            entity.Set(FieldRules, rules.Serialize());
            storage.SetEntity(entity);
        }

        private static Entity FindTracingRulesEntity(Document doc, out DataStorage storage)
        {
            storage = null;
            Schema schema = Schema.Lookup(TracingRulesSchemaGuid);
            if (doc == null || schema == null) return null;

            foreach (DataStorage candidate in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
            {
                Entity entity = candidate.GetEntity(schema);
                if (IsOwnedBy(entity, candidate))
                {
                    storage = candidate;
                    return entity;
                }
            }

            return null;
        }

        private static Schema GetTracingRulesSchema()
        {
            Schema schema = Schema.Lookup(TracingRulesSchemaGuid);
            if (schema != null) return schema;

            SchemaBuilder builder = new SchemaBuilder(TracingRulesSchemaGuid);
            builder.SetSchemaName("CameraFovTracingRules");
            builder.SetDocumentation("Which obstacle categories Camera FOV traces into Boundary lines in this project.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldOwnerUniqueId, typeof(string));
            builder.AddSimpleField(FieldRules, typeof(string));
            return builder.Finish();
        }

        #endregion

        #region Traced boundary lines

        /// <summary>
        /// Marks a detail curve as generated by automatic boundary tracing in the given view.
        /// Must be called inside an open transaction.
        /// </summary>
        public static void TagTracedBoundaryLine(CurveElement curve, View view)
        {
            if (curve == null || view == null) return;

            Entity entity = new Entity(GetTracedBoundarySchema());
            entity.Set(FieldOwnerUniqueId, curve.UniqueId);
            entity.Set(FieldViewUniqueId, view.UniqueId);
            curve.SetEntity(entity);
        }

        /// <summary>
        /// Returns true when the curve was generated by automatic tracing in this view.
        /// User-drawn lines, lines traced by older versions and copied lines return false.
        /// </summary>
        public static bool IsTracedBoundaryLine(CurveElement curve, View view)
        {
            if (curve == null || view == null) return false;

            Schema schema = Schema.Lookup(TracedBoundarySchemaGuid);
            if (schema == null) return false;

            Entity entity = curve.GetEntity(schema);
            return IsOwnedBy(entity, curve) && entity.Get<string>(FieldViewUniqueId) == view.UniqueId;
        }

        #endregion

        private static bool IsOwnedBy(Entity entity, Element element)
        {
            return entity != null
                && entity.IsValid()
                && entity.Get<string>(FieldOwnerUniqueId) == element.UniqueId;
        }

        private static Schema GetCoverageSchema()
        {
            Schema schema = Schema.Lookup(CoverageSchemaGuid);
            if (schema != null) return schema;

            SchemaBuilder builder = new SchemaBuilder(CoverageSchemaGuid);
            builder.SetSchemaName("CameraFovCoverageRegion");
            builder.SetDocumentation("Links a Camera FOV coverage filled region to its camera, DORI type and view.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldOwnerUniqueId, typeof(string));
            builder.AddSimpleField(FieldViewUniqueId, typeof(string));
            builder.AddSimpleField(FieldCameraUniqueId, typeof(string));
            builder.AddSimpleField(FieldRegionTypeUniqueId, typeof(string));
            return builder.Finish();
        }

        private static Schema GetCoverageSourceSchema()
        {
            Schema schema = Schema.Lookup(CoverageSourceSchemaGuid);
            if (schema != null) return schema;

            SchemaBuilder builder = new SchemaBuilder(CoverageSourceSchemaGuid);
            builder.SetSchemaName("CameraFovCoverageSource");
            builder.SetDocumentation("What a Camera FOV coverage region was drawn from, to detect when it is out of date.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldOwnerUniqueId, typeof(string));
            builder.AddSimpleField(FieldCameraState, typeof(string));
            builder.AddSimpleField(FieldBoundaryState, typeof(string));
            builder.AddSimpleField(FieldReach, typeof(string));
            return builder.Finish();
        }

        private static Schema GetTracedBoundarySchema()
        {
            Schema schema = Schema.Lookup(TracedBoundarySchemaGuid);
            if (schema != null) return schema;

            SchemaBuilder builder = new SchemaBuilder(TracedBoundarySchemaGuid);
            builder.SetSchemaName("CameraFovTracedBoundaryLine");
            builder.SetDocumentation("Marks a Boundary detail line generated by Camera FOV automatic tracing.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldOwnerUniqueId, typeof(string));
            builder.AddSimpleField(FieldViewUniqueId, typeof(string));
            return builder.Finish();
        }
    }
}
