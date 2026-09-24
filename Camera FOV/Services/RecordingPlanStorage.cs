using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using System;
using System.Linq;

namespace Camera_FOV.Services
{
    /// <summary>The project's recording plan (issue #19), on one DataStorage element owned by the plugin.</summary>
    public static class RecordingPlanStorage
    {
        // Schema GUIDs must never change once released: existing projects reference them.
        private static readonly Guid SchemaGuid = new Guid("C2E7A5B8-1F3D-4A96-9B0E-4D8F6C2A1E73");
        private const string FieldPlan = "Plan";

        public static string Load(Document doc)
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (doc == null || schema == null) return null;

            foreach (DataStorage storage in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
            {
                Entity entity = storage.GetEntity(schema);
                if (entity.IsValid()) return entity.Get<string>(FieldPlan);
            }
            return null;
        }

        /// <summary>Must be called inside an open transaction.</summary>
        public static void Save(Document doc, string json)
        {
            Schema schema = GetSchema();
            DataStorage storage = new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .FirstOrDefault(s => s.GetEntity(schema).IsValid()) ?? DataStorage.Create(doc);

            var entity = new Entity(schema);
            entity.Set(FieldPlan, json ?? string.Empty);
            storage.SetEntity(entity);
        }

        private static Schema GetSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            SchemaBuilder builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("CameraFovRecordingPlan");
            builder.SetDocumentation("How the project's cameras record, for the Camera FOV storage estimate.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldPlan, typeof(string));
            return builder.Finish();
        }
    }
}
