namespace Njulf.Core.Interfaces
{
    public interface IContentManager
    {
        T Load<T>(string path);
        void Unload<T>(T asset);
        void Clear();
    }
}
