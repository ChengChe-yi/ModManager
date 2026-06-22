namespace ModManager.Core.Messages
{
	public class OverlayStyleChangedMessage(bool isAcrylicEnabled)
	{
		public bool Value { get; } = isAcrylicEnabled;
	}
}