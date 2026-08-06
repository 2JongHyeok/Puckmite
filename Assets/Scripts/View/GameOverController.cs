using Puckmite.Game;

namespace Puckmite.View
{
    /// <summary>Shown when the player goes down (사용자 지정). The only way out is back to the title;
    /// the next start begins a fresh campaign there (design doc 2.1: no continue).</summary>
    public sealed class GameOverController : SimpleScreenController
    {
        protected override string Heading => "Game Over";
        protected override string ButtonLabel => "타이틀로";

        protected override void OnButton()
        {
            GameFlow.LoadTitle();
        }
    }
}
