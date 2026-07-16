namespace Njulf.Rendering.Diagnostics
{
    public sealed class RenderBudgetSettings
    {
        private RenderBudgetProfileKind _activeProfile = RenderBudgetProfileKind.Development;

        public bool Enabled { get; set; } = true;

        public RenderBudgetProfileKind ActiveProfile
        {
            get => _activeProfile;
            set
            {
                _activeProfile = value;
                Profile = RenderBudgetProfile.GetDefault(value);
            }
        }

        public RenderBudgetProfile Profile { get; private set; } = RenderBudgetProfile.Development;
    }
}
