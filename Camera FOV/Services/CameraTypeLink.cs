using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Camera_FOV.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Camera_FOV.Services
{
    /// <summary>What applying a camera type to a Revit family did.</summary>
    public sealed class CameraTypeApplyResult
    {
        public FamilySymbol Symbol;
        public bool Created;
        public List<string> Written = new List<string>();
        public List<string> Missing = new List<string>();   // No such type parameter in the family
        public List<string> Failed = new List<string>();    // Present, but the value couldn't be set
    }

    /// <summary>
    /// Links camera types from the library to Revit family types. Applying a camera type duplicates a
    /// type of the chosen family under the camera type's name (or updates it when it exists), writes
    /// the resolution, the widest horizontal field of view and the custom parameters to the type
    /// parameters of the same names, and stores a copy of the camera type on the Revit type. That copy
    /// is what the Camera FOV window reads, so a project keeps its ranges without the library.
    /// </summary>
    public static class CameraTypeLink
    {
        // Schema GUIDs must never change once released: existing projects reference them.
        private static readonly Guid SchemaGuid = new Guid("3F8C2D71-6A4E-4B9F-A1D2-7E5C0B9F4A63");
        private const string FieldId = "CameraTypeId";
        private const string FieldSnapshot = "Snapshot";

        /// <summary>The camera type linked to a camera instance's family type, or null.</summary>
        public static CameraType GetLinked(Element camera)
        {
            if (camera == null) return null;
            return GetSnapshot(camera.Document.GetElement(camera.GetTypeId()));
        }

        public static CameraType GetSnapshot(Element type)
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (type == null || schema == null) return null;

            Entity entity = type.GetEntity(schema);
            return entity.IsValid() ? CameraTypeLibrary.Deserialize(entity.Get<string>(FieldSnapshot)) : null;
        }

        /// <summary>Every family type in the project linked to a camera type, with the copy it holds.</summary>
        public static List<(FamilySymbol Symbol, CameraType Snapshot)> FindLinkedTypes(Document doc)
        {
            var result = new List<(FamilySymbol, CameraType)>();
            if (doc == null || Schema.Lookup(SchemaGuid) == null) return result;

            foreach (FamilySymbol symbol in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
            {
                CameraType snapshot = GetSnapshot(symbol);
                if (snapshot != null) result.Add((symbol, snapshot));
            }
            return result;
        }

        /// <summary>Loadable families in the Security Devices category: where camera types can be applied.</summary>
        public static List<Family> CameraFamilies(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(f => f.FamilyCategory?.BuiltInCategory == BuiltInCategory.OST_SecurityDevices)
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Creates or updates the family type for a camera type. Must be called inside an open transaction.
        /// </summary>
        public static CameraTypeApplyResult Apply(Document doc, Family family, CameraType cameraType)
        {
            var result = new CameraTypeApplyResult();
            string name = cameraType.Name.Trim();

            List<FamilySymbol> symbols = family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().ToList();
            if (!symbols.Any())
                throw new InvalidOperationException($"The family “{family.Name}” has no types to duplicate.");

            result.Symbol = symbols.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (result.Symbol == null)
            {
                // A type already linked to another camera type makes a poorer template than a plain one
                FamilySymbol template = symbols.FirstOrDefault(s => GetSnapshot(s) == null) ?? symbols[0];
                result.Symbol = (FamilySymbol)template.Duplicate(name);
                result.Created = true;
            }

            FamilySymbol symbol = result.Symbol;
            Set(symbol, SettingsManager.Settings.ParameterName_Resolution, cameraType.HorizontalResolution.ToString(CultureInfo.InvariantCulture), result, pixels: true);
            SetAngle(symbol, SettingsManager.Settings.ParameterName_StandardFOV, cameraType.HorizontalFovMax, result);
            foreach (CustomParameter parameter in cameraType.CustomParameters.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                Set(symbol, parameter.Name.Trim(), parameter.Value ?? string.Empty, result);

            Entity entity = new Entity(GetSchema());
            entity.Set(FieldId, cameraType.Id);
            entity.Set(FieldSnapshot, CameraTypeLibrary.Serialize(cameraType));
            symbol.SetEntity(entity);

            return result;
        }

        private static void SetAngle(FamilySymbol symbol, string name, double degrees, CameraTypeApplyResult result)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            Parameter parameter = symbol.LookupParameter(name);
            if (parameter == null) { result.Missing.Add(name); return; }

            if (!parameter.IsReadOnly && parameter.StorageType == StorageType.Double && parameter.Set(degrees * Math.PI / 180.0))
                result.Written.Add(name);
            else
                result.Failed.Add(name);
        }

        // Text is written as is; numbers in the units Revit shows (e.g. "2500 mm", "45°"), yes/no as
        // yes/no, true/false or 1/0.
        private static void Set(FamilySymbol symbol, string name, string value, CameraTypeApplyResult result, bool pixels = false)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            Parameter parameter = symbol.LookupParameter(name);
            if (parameter == null) { result.Missing.Add(name); return; }
            if (parameter.IsReadOnly) { result.Failed.Add(name); return; }

            bool ok = false;
            value = value.Trim();
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    ok = parameter.Set(value);
                    break;

                case StorageType.Integer:
                    if (TryParseYesNo(value, out int flag) && IsYesNo(parameter))
                        ok = parameter.Set(flag);
                    else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                        ok = parameter.Set(number);
                    break;

                case StorageType.Double:
                    if (pixels)
                    {
                        ok = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw) && parameter.Set(raw);
                        break;
                    }
                    try { ok = parameter.SetValueString(value); }
                    catch (Exception) { ok = false; }
                    if (!ok && TryParseNumber(value, out double shown))
                    {
                        try { ok = parameter.Set(UnitUtils.ConvertToInternalUnits(shown, parameter.GetUnitTypeId())); }
                        catch (Exception) { ok = parameter.Set(shown); } // A unitless number
                    }
                    break;
            }

            (ok ? result.Written : result.Failed).Add(name);
        }

        private static bool IsYesNo(Parameter parameter)
        {
            try { return parameter.Definition.GetDataType() == SpecTypeId.Boolean.YesNo; }
            catch (Exception) { return false; }
        }

        private static bool TryParseYesNo(string value, out int flag)
        {
            switch (value.ToLowerInvariant())
            {
                case "yes": case "true": case "1": case "jah": flag = 1; return true;
                case "no": case "false": case "0": case "ei": flag = 0; return true;
                default: flag = 0; return false;
            }
        }

        private static bool TryParseNumber(string value, out double number)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number);
        }

        private static Schema GetSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            SchemaBuilder builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("CameraFovCameraType");
            builder.SetDocumentation("The Camera FOV library camera type a family type was created from, with a copy of its values.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldId, typeof(string));
            builder.AddSimpleField(FieldSnapshot, typeof(string));
            return builder.Finish();
        }
    }
}
