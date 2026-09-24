using Camera_FOV.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Camera_FOV.Services
{
    /// <summary>Saving would overwrite a newer version someone else saved since this one was loaded.</summary>
    public sealed class CameraTypeConflictException : Exception
    {
        public CameraType Newer { get; }

        public CameraTypeConflictException(CameraType newer)
            : base($"“{newer.Name}” was changed by {newer.ModifiedBy ?? "someone else"} since it was opened.")
        {
            Newer = newer;
        }
    }

    /// <summary>
    /// The camera type library: one JSON file per type in a folder, so several people can share it
    /// through a network drive or a synced folder. Each save checks the type's version on disk first,
    /// so a type changed by someone else meanwhile is reported instead of overwritten. Deleted types
    /// are moved to a "deleted" subfolder, where they can be restored by moving them back.
    /// </summary>
    public static class CameraTypeLibrary
    {
        public static readonly string LocalFolder = @"C:\ProgramData\RK Tools\Camera FOV\Camera types";

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace
        };

        /// <summary>The folder in Settings, or this PC's own folder when none is set.</summary>
        public static string Folder
        {
            get
            {
                string shared = SettingsManager.Settings.CameraTypesFolder?.Trim();
                return string.IsNullOrEmpty(shared) ? LocalFolder : shared;
            }
        }

        public static bool IsShared => !string.IsNullOrWhiteSpace(SettingsManager.Settings.CameraTypesFolder);

        /// <summary>Every type in the library, by name. Files that can't be read are returned as problems.</summary>
        public static List<CameraType> LoadAll(out List<string> unreadable)
        {
            unreadable = new List<string>();
            var types = new List<CameraType>();
            if (!Directory.Exists(Folder)) return types;

            foreach (string file in Directory.GetFiles(Folder, "*.json"))
            {
                try
                {
                    CameraType type = JsonConvert.DeserializeObject<CameraType>(File.ReadAllText(file), Json);
                    if (type != null && !string.IsNullOrEmpty(type.Id)) types.Add(type);
                    else unreadable.Add(Path.GetFileName(file));
                }
                catch (Exception)
                {
                    unreadable.Add(Path.GetFileName(file));
                }
            }

            return types.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>The type as it is on disk now, or null when it isn't there.</summary>
        public static CameraType Load(string id)
        {
            string file = PathFor(id);
            return File.Exists(file) ? JsonConvert.DeserializeObject<CameraType>(File.ReadAllText(file), Json) : null;
        }

        /// <summary>
        /// Saves the type when the file still holds the version it was loaded at (Version; 0 for a new
        /// type), then returns it with the next version number. Otherwise throws
        /// CameraTypeConflictException with the newer version, and nothing is written.
        /// </summary>
        public static CameraType Save(CameraType type)
        {
            Directory.CreateDirectory(Folder);
            string file = PathFor(type.Id);

            // Held while checking and writing, so two people saving the same type at once can't both pass the check
            using (OpenLock(file + ".lock"))
            {
                CameraType onDisk = File.Exists(file) ? JsonConvert.DeserializeObject<CameraType>(File.ReadAllText(file), Json) : null;
                if ((onDisk?.Version ?? 0) != type.Version)
                    throw new CameraTypeConflictException(onDisk ?? type);

                CameraType saved = type.Clone();
                saved.Name = saved.Name.Trim();
                saved.Version = type.Version + 1;
                saved.ModifiedBy = Environment.UserName;
                saved.ModifiedUtc = DateTime.UtcNow;

                // Written next to the target and swapped in, so a reader never sees half a file
                string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(saved, Json));
                try
                {
                    if (File.Exists(file)) File.Replace(temp, file, null);
                    else File.Move(temp, file);
                }
                catch (Exception) when (File.Exists(temp))
                {
                    // Some network shares don't support replacing in one step
                    File.Copy(temp, file, true);
                    File.Delete(temp);
                }

                return saved;
            }
        }

        // An exclusive lock file, retried for a few seconds while someone else holds it
        private static FileStream OpenLock(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                }
                catch (IOException) when (attempt < 20)
                {
                    System.Threading.Thread.Sleep(150);
                }
            }
        }

        /// <summary>Moves the type to the "deleted" subfolder.</summary>
        public static void Delete(CameraType type)
        {
            string file = PathFor(type.Id);
            if (!File.Exists(file)) return;

            string deleted = Path.Combine(Folder, "deleted");
            Directory.CreateDirectory(deleted);
            string target = Path.Combine(deleted, Path.GetFileName(file));
            if (File.Exists(target)) File.Delete(target); // An older deleted copy of the same type
            File.Move(file, target);
        }

        private static string PathFor(string id) => Path.Combine(Folder, id + ".json");

        public static string Serialize(CameraType type) => JsonConvert.SerializeObject(type, Formatting.None, Json);

        public static CameraType Deserialize(string json)
        {
            try { return string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<CameraType>(json, Json); }
            catch (JsonException) { return null; }
        }
    }
}
