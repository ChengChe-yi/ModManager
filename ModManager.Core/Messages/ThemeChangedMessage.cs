namespace ModManager.Core.Messages
{
	public class ThemeChangedMessage(int themeIndex)
	{
		public int Value { get; } = themeIndex;
	}
}