using Puckmite.Game;

namespace Puckmite.View
{
    /// <summary>Shown when all three stages are cleared (사용자 지정). Back to the title, where the
    /// next start begins a fresh campaign.</summary>
    public sealed class GameClearController : SimpleScreenController
    {
        protected override string Heading => "Game Clear!";
        protected override string ButtonLabel => "타이틀로";

        protected override void OnButton()
        {
            GameFlow.LoadTitle();
        }
    }
}
