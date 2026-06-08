namespace Njulf.Core.Interfaces
{
    public interface IUpdateable
    {
        bool Enabled { get; set; }
        int UpdateOrder { get; set; }
        
        void Update(float deltaTime);
    }
}
