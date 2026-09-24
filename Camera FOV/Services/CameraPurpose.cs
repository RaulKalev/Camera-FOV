using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Camera_FOV.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Camera_FOV.Services
{
    /// <summary>
    /// What a camera is for (issue #14): the observation category it should reach and the risk grade
    /// of its function (EVS-EN IEC 62676-4:2026, 4.2.3 and Annex D). Kept in the camera's text
    /// parameters when the family has them, so they show in schedules; otherwise on the camera itself
    /// in the plugin's own storage.
    /// </summary>
    public static class CameraPurpose
    {
        public static readonly IReadOnlyList<string> RiskGrades = new[] { "BR", "SG1", "SG2", "SG3", "SG4" };

        // Schema GUIDs must never change once released: existing projects reference them.
        private static readonly Guid SchemaGuid = new Guid("9A4C1E62-3B7D-4F0A-8E25-6D1B9C3F7A48");
        private const string FieldCategory = "Category";
        private const string FieldRiskGrade = "RiskGrade";

        /// <summary>The category named in text such as "Validate" or "validate (500 px/m)", or null.</summary>
        public static ObservationCategory ParseCategory(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string trimmed = text.Trim();
            return CameraData.Categories.FirstOrDefault(c => trimmed.StartsWith(c.Name, StringComparison.OrdinalIgnoreCase));
        }

        public static string ParseRiskGrade(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string compact = text.Replace(" ", string.Empty).ToUpperInvariant();
            return RiskGrades.FirstOrDefault(g => compact == g) ?? RiskGrades.FirstOrDefault(g => compact.StartsWith(g));
        }

        public static (ObservationCategory Category, string RiskGrade) Read(Element camera)
        {
            if (camera == null) return (null, null);

            string category = ReadText(camera, SettingsManager.Settings.ParameterName_IntendedCategory, FieldCategory);
            string grade = ReadText(camera, SettingsManager.Settings.ParameterName_RiskGrade, FieldRiskGrade);
            return (ParseCategory(category), ParseRiskGrade(grade));
        }

        /// <summary>Whether the family has writable text parameters for both values, so they show in schedules.</summary>
        public static bool HasParameters(Element camera)
        {
            return IsWritableText(camera?.LookupParameter(SettingsManager.Settings.ParameterName_IntendedCategory ?? string.Empty))
                && IsWritableText(camera?.LookupParameter(SettingsManager.Settings.ParameterName_RiskGrade ?? string.Empty));
        }

        /// <summary>Stores the values on the camera. Must be called inside an open transaction.</summary>
        public static void Write(Element camera, ObservationCategory category, string riskGrade)
        {
            string categoryText = category?.Name ?? string.Empty;
            string gradeText = riskGrade ?? string.Empty;

            bool categoryInParameter = TrySetText(camera, SettingsManager.Settings.ParameterName_IntendedCategory, categoryText);
            bool gradeInParameter = TrySetText(camera, SettingsManager.Settings.ParameterName_RiskGrade, gradeText);

            // Whatever has no parameter is kept in the plugin's storage; the storage is cleared otherwise
            Schema schema = GetSchema();
            if (categoryInParameter && gradeInParameter)
            {
                if (camera.GetEntity(schema).IsValid()) camera.DeleteEntity(schema);
                return;
            }

            var entity = new Entity(schema);
            entity.Set(FieldCategory, categoryInParameter ? string.Empty : categoryText);
            entity.Set(FieldRiskGrade, gradeInParameter ? string.Empty : gradeText);
            camera.SetEntity(entity);
        }

        /// <summary>
        /// Whether a camera reaches a category, and over which plan distances: from the edge of its
        /// dead zone out to where the density falls to the category's px/m (or the top of its view).
        /// When it doesn't, the text says what it would need.
        /// </summary>
        public static (bool Met, double FromMeters, double ToMeters, string Text) Reach(int resolution, double fovDegrees, CameraMount mount, ObservationCategory category)
        {
            double slant = CameraData.DistanceMeters(resolution, fovDegrees, category.PixelsPerMeter, CameraData.CurrentFormula);
            var (near, far) = mount?.VisibleRange() ?? (0, double.PositiveInfinity);
            double? plan = mount != null ? mount.PlanForSlant(slant) : slant;

            if (!plan.HasValue)
            {
                double above = mount.HeightAboveTarget ?? 0;
                return (false, 0, 0,
                    $"Out of reach: {category.Name} needs the target within {slant:0.0} m of the lens, but the camera is {above:0.0} m above it. Lower the camera, narrow the view or use more pixels.");
            }

            double to = Math.Min(plan.Value, far);
            if (double.IsPositiveInfinity(near) || to <= near)
            {
                string why = double.IsPositiveInfinity(near)
                    ? "its view doesn’t reach the target height at all"
                    : $"the dead zone under it reaches {near:0.0} m";
                return (false, 0, 0,
                    $"Out of reach: {category.Name} needs the target within {plan.Value:0.0} m in plan, but {why}. Tilt the camera further down or lower it.");
            }

            string range = near > 0.05 ? $"from {near:0.0} to {to:0.0} m" : $"up to {to:0.0} m";
            return (true, near, to, $"{category.Name} reached {range}.");
        }

        private static string ReadText(Element camera, string parameterName, string field)
        {
            Parameter parameter = string.IsNullOrWhiteSpace(parameterName) ? null : camera.LookupParameter(parameterName);
            string value = parameter != null && parameter.StorageType == StorageType.String ? parameter.AsString() : null;
            if (!string.IsNullOrWhiteSpace(value)) return value;

            Schema schema = Schema.Lookup(SchemaGuid);
            Entity entity = schema != null ? camera.GetEntity(schema) : null;
            return entity != null && entity.IsValid() ? entity.Get<string>(field) : null;
        }

        private static bool IsWritableText(Parameter parameter) =>
            parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.String;

        private static bool TrySetText(Element camera, string parameterName, string value)
        {
            Parameter parameter = string.IsNullOrWhiteSpace(parameterName) ? null : camera.LookupParameter(parameterName);
            return IsWritableText(parameter) && parameter.Set(value);
        }

        private static Schema GetSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            SchemaBuilder builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("CameraFovCameraPurpose");
            builder.SetDocumentation("A camera's intended observation category and risk grade, when its family has no parameters for them.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldCategory, typeof(string));
            builder.AddSimpleField(FieldRiskGrade, typeof(string));
            return builder.Finish();
        }
    }
}
