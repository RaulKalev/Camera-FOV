using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Camera_FOV.Models
{
    /// <summary>
    /// How each kind of obstacle is traced into Boundary lines, i.e. whether it blocks the camera's
    /// view in the 2D coverage. Stored per project (see ElementTagStorage), so everyone working on a
    /// model traces it the same way. This is a plan-view model: it cannot tell what a camera really
    /// sees in 3D, only which footprints are treated as opaque.
    /// </summary>
    public sealed class TracingRules
    {
        public bool Walls { get; }
        public bool Columns { get; }
        public bool CurtainPanels { get; }   // False: glazing is treated as see-through
        public bool Mullions { get; }
        public bool CloseDoors { get; }      // True: a line across the opening; false: the opening stays open
        public bool CloseWindows { get; }

        // Today's behaviour: everything blocks, door and window openings are closed with a line
        public static TracingRules Default { get; } = new TracingRules(true, true, true, true, true, true);

        public TracingRules(bool walls, bool columns, bool curtainPanels, bool mullions, bool closeDoors, bool closeWindows)
        {
            Walls = walls;
            Columns = columns;
            CurtainPanels = curtainPanels;
            Mullions = mullions;
            CloseDoors = closeDoors;
            CloseWindows = closeWindows;
        }

        /// <summary>Categories whose solids are traced where they cross the cut plane.</summary>
        public List<BuiltInCategory> TracedCategories()
        {
            var categories = new List<BuiltInCategory>();
            if (Walls) categories.Add(BuiltInCategory.OST_Walls);
            if (Columns)
            {
                categories.Add(BuiltInCategory.OST_StructuralColumns);
                categories.Add(BuiltInCategory.OST_Columns);
            }
            if (CurtainPanels) categories.Add(BuiltInCategory.OST_CurtainWallPanels);
            if (Mullions) categories.Add(BuiltInCategory.OST_CurtainWallMullions);
            return categories;
        }

        /// <summary>Door and window categories whose openings get a closing line.</summary>
        public List<BuiltInCategory> ClosedOpeningCategories()
        {
            var categories = new List<BuiltInCategory>();
            if (CloseDoors) categories.Add(BuiltInCategory.OST_Doors);
            if (CloseWindows) categories.Add(BuiltInCategory.OST_Windows);
            return categories;
        }

        public bool TracesNothing => !TracedCategories().Any() && !ClosedOpeningCategories().Any();

        /// <summary>One line per category for the trace summary.</summary>
        public string Describe()
        {
            string Blocks(bool value) => value ? "block" : "ignored";
            return $"Walls {Blocks(Walls)}, columns {Blocks(Columns)}, mullions {Blocks(Mullions)}, " +
                   $"curtain panels {(CurtainPanels ? "block" : "see-through")}, " +
                   $"doors {(CloseDoors ? "closed" : "open")}, windows {(CloseWindows ? "closed" : "open")}.";
        }

        // "walls=1;columns=1;…", tolerant of missing or unknown keys so older and newer versions can read it
        public string Serialize()
        {
            return string.Join(";", new[]
            {
                $"walls={Flag(Walls)}", $"columns={Flag(Columns)}", $"panels={Flag(CurtainPanels)}",
                $"mullions={Flag(Mullions)}", $"doors={Flag(CloseDoors)}", $"windows={Flag(CloseWindows)}"
            });
        }

        public static TracingRules Parse(string text)
        {
            var values = new Dictionary<string, bool>();
            foreach (string pair in (text ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = pair.Split('=');
                if (parts.Length == 2) values[parts[0].Trim()] = parts[1].Trim() == "1";
            }

            bool Get(string key, bool fallback) => values.TryGetValue(key, out bool value) ? value : fallback;
            TracingRules d = Default;
            return new TracingRules(
                Get("walls", d.Walls), Get("columns", d.Columns), Get("panels", d.CurtainPanels),
                Get("mullions", d.Mullions), Get("doors", d.CloseDoors), Get("windows", d.CloseWindows));
        }

        public override bool Equals(object obj) => obj is TracingRules other && other.Serialize() == Serialize();
        public override int GetHashCode() => Serialize().GetHashCode();

        private static string Flag(bool value) => value ? "1" : "0";
    }
}
