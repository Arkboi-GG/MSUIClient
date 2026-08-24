namespace MSUIClient.Engine;

/// <summary>
/// Separates the body that exists in the game world from the local controller. In ordinary
/// embodied play the controller is that body's predicted pose. In pending SUI states and Free
/// View the controller is parked or is the observer rig, so the streamed entity owns the pose.
/// </summary>
public static class WorldBodyPoseLaw
{
    public static bool ControllerOwnsPose(bool freeView, bool stableEmbodiedControlState,
        bool queriedControlledBody, bool controllerMovementAuthoritative) =>
        !freeView && stableEmbodiedControlState && queriedControlledBody &&
        controllerMovementAuthoritative;
}
