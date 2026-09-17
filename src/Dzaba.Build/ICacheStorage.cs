namespace Dzaba.Build;

public interface ICacheStorage
{
    bool Exists(string cacheKey);

    void Fetch(string cacheKey, string destinationDirectory);

    void Publish(string cacheKey, string sourceDirectory);
}
