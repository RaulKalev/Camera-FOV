namespace Camera_FOV.UI
{
    public partial class CoverageAuditWindow
    {
        private void ExportTestPlan()
        {
            MessageDialog.ShowInfo("Test plan", "The test plan export isn’t available yet.", owner: this);
        }

        private void ExportSchedule()
        {
            MessageDialog.ShowInfo("Camera schedule", "The camera schedule export isn’t available yet.", owner: this);
        }
    }
}
