using UnityEngine;
using System.Linq;
using System.Collections;

public class Entity : MonoBehaviour
{
    public EntityData data;
    private Stat[] stats;

    void Start()
    {
        stats = new Stat[data.stats.Length];
        for (int i = 0; i < stats.Length; i++)
        {
            stats[i] = data.stats[i].Clone();
            DoTick(stats[i]);
        }

        ChangeStat<Health>(0f);
    }

    public void DoTick(Stat stat)
    {
        StartCoroutine(Tick(stat));
    }

    public IEnumerator Tick(Stat stat)
    {
        yield return null;
        stat.Tick(this, Time.deltaTime);
    }

    public T GetStat<T>() where T : Stat
    {
        return stats.OfType<T>().FirstOrDefault();
    }

    public void ChangeStat<T>(float amount) where T : Stat
    {
        GetStat<T>()?.Change(this, amount);
    }

    public void RemoveStat<T>() where T : Stat
    {
        var stat = GetStat<T>();
        if (stat != null)
        {
            stats = stats.Where(s => s != stat).ToArray();
        }
    }

    public void AddStat(Stat stat)
    {
        if(stats.Contains(stat) == false)
        {
            var newStats = new Stat[stats.Length + 1];
            stats.CopyTo(newStats, 0);
            newStats[newStats.Length - 1] = stat;
            stats = newStats;
            DoTick(stat);
        }
    }

    public void Kill()
    {
        Destroy(gameObject);
    }
}
