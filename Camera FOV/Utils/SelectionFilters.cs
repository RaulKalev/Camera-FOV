using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace Camera_FOV.Utils
{
    public class SecurityDevicesSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element element)
        {
            // Check if the element's category is Security Devices
            return element?.Category != null && element.Category.Name == "Security Devices";
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            // Allow references (not used in this example)
            return false;
        }
    }
}
