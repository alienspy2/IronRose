namespace IronRose.Scripting
{
    public interface IHotReloadable
    {
        string SerializeState();    // TOML 형식으로 반환
        void DeserializeState(string toml);
    }
}
