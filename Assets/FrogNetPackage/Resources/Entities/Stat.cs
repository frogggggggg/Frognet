using System;
using UnityEngine;
using System.Collections;

[Serializable]
public abstract class Stat
{
    public float value;
    public float min;
    public float max;
    public float grow;


    public void Tick(Entity entity, float deltaTime)
    {
        if (grow != 0f) Change(entity, grow * deltaTime);
        OnTick(entity, deltaTime);
    }

    public void Change(Entity entity, float amount)
    {
        value = Mathf.Clamp(value + amount, min, max);
        float pastValue = value;
        OnChanged(entity);

        if(value != pastValue) entity.DoTick(this);
    }

    protected virtual void OnChanged(Entity entity) { }
    protected virtual void OnTick(Entity entity, float deltaTime) { }

    public Stat Clone() => (Stat)MemberwiseClone();
}

[Serializable]
public class Health : Stat
{
    protected override void OnChanged(Entity entity)
    {
        if (value <= min) entity.Kill();
    }

}

[Serializable]
public class Stamina : Stat { }

[Serializable]
public class Damage : Stat { }

[Serializable]
public class Buff_Damage : Stat
{
    public float time;
    protected override void OnTick(Entity entity, float deltaTime)
    {
        if (time > 0f)
        {
            time -= deltaTime;
            if (time <= 0f)
            {
                entity.RemoveStat<Buff_Damage>();
            }
        }
    }
}
