using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private TutorialFlagsState? _tutorialFlags;
    private void ApplyTutorialFlags(byte[] body) => (_tutorialFlags ??= new()).Apply(body);

    // These are account preferences on the authenticated socket, including while
    // driving a companion. No source-less proxy may replace this account's flags.
    private bool ChangeTutorialFlags(Op opcode, uint wireBit = 0)
    {
        if (_tutorialFlags is not { Known: true } flags || _net is null) return false;
        switch (opcode)
        {
            case Op.CMSG_TUTORIAL_FLAG:
                if (wireBit >= 256 || !_net.MarkTutorial(wireBit)) return false;
                flags.Mark(wireBit); return true;
            case Op.CMSG_TUTORIAL_CLEAR:
                if (!_net.ClearTutorials()) return false;
                flags.DisableAll(); return true;
            case Op.CMSG_TUTORIAL_RESET:
                if (!_net.ResetTutorials()) return false;
                flags.EnableAll(); return true;
            default: return false;
        }
    }
}
