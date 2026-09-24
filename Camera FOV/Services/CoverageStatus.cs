using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;

namespace Camera_FOV.Services
{
    public enum CoverageState
    {
        None,          // No generated coverage for the camera in this view
        Current,       // Matches the camera and the nearby Boundary lines
        Stale,         // The camera changed since the coverage was drawn
        NeedsReview,   // The camera is unchanged, but Boundary lines within reach changed
        Unknown,       // Drawn before the plugin recorded what coverage was drawn from
        CameraMissing  // The camera no longer exists
    }

    /// <summary>
    /// Whether a camera's generated coverage in a view still matches the camera. Read-only: nothing
    /// is changed or updated automatically.
    /// </summary>
    public class CoverageStatus
    {
        public CoverageState State { get; private set; }
        public Element Camera { get; private set; }
        public List<ElementId> Regions { get; private set; } = new List<ElementId>();
        public List<ElementId> RegionTypeIds { get; private set; } = new List<ElementId>();
        public List<string> Changes { get; private set; } = new List<string>();

        /// <summary>
        /// Whether the direction, FOV and resolution the coverage was drawn with still describe the
        /// camera: nothing about the camera changed, though Boundary lines or the pixel density
        /// formula may have.
        /// </summary>
        public bool DrawnValuesApply =>
            State == CoverageState.Current || State == CoverageState.NeedsReview ||
            (State == CoverageState.Stale && Changes.All(c => c == CoverageSource.FormulaChangeLabel));

        public static CoverageStatus Evaluate(Document doc, View view, Element camera, IEnumerable<ElementId> regionIds)
        {
            List<Element> regions = regionIds
                .Select(doc.GetElement)
                .Where(e => e != null)
                .ToList();

            var status = new CoverageStatus
            {
                Camera = camera,
                Regions = regions.Select(r => r.Id).ToList(),
                RegionTypeIds = regions.Select(r => r.GetTypeId()).Distinct().ToList()
            };

            if (!regions.Any())
            {
                status.State = CoverageState.None;
                return status;
            }

            if (camera == null)
            {
                status.State = CoverageState.CameraMissing;
                return status;
            }

            string currentCamera = CoverageSource.CaptureCameraState(camera);
            XYZ position = CoverageSource.GetCameraPosition(camera);
            bool anySource = false;
            bool boundariesChanged = false;
            var boundaryChecks = new Dictionary<string, string>(); // reach -> current fingerprint, computed once

            foreach (Element region in regions)
            {
                if (!ElementTagStorage.TryGetCoverageSource(region, out string storedCamera, out string storedBoundary, out double reach))
                    continue;

                anySource = true;
                foreach (string change in CoverageSource.CompareCameraStates(storedCamera, currentCamera))
                {
                    if (!status.Changes.Contains(change))
                        status.Changes.Add(change);
                }

                string reachKey = reach.ToString("R");
                if (!boundaryChecks.TryGetValue(reachKey, out string currentBoundary))
                    boundaryChecks[reachKey] = currentBoundary = CoverageSource.CaptureBoundaryState(doc, view, position, reach);

                if (currentBoundary != storedBoundary)
                    boundariesChanged = true;
            }

            if (!anySource)
                status.State = CoverageState.Unknown;
            else if (status.Changes.Any())
                status.State = CoverageState.Stale;
            else if (boundariesChanged)
                status.State = CoverageState.NeedsReview;
            else
                status.State = CoverageState.Current;

            return status;
        }
    }
}
