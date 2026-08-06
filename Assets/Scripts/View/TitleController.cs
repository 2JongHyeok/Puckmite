using Puckmite.Game;

namespace Puckmite.View
{
    /// <summary>The entry scene (사용자 지정): the game's name a little above centre, the start button
    /// under it. Starting is always a fresh campaign.</summary>
    public sealed class TitleController : SimpleScreenController
    {
        protected override string Heading => "Puckmite";
        protected override string ButtonLabel => "게임 시작";

        protected override void OnButton()
        {
            GameFlow.StartNewCampaign();
        }
    }
}
