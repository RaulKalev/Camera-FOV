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
