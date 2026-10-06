// 池化对象生命周期回调。
// 池只管"取/还"两个动作，对象自己需要在取出时重置、归还时收尾；
// 用接口而不是让池去猜组件，池就不必知道敌人、道具、特效的区别。
// 刻意不继承 IDisposable：Dispose 语义是"销毁"，与"归还复用"正好相反，容易误用。
public interface IPooledObject
{
    /// <summary>对象从池中取出、即将放到世界之前调用。用于重置状态。</summary>
    void OnSpawnedFromPool();

    /// <summary>对象被归还进池、即将失活之前调用。用于停下协程、清空速度、断开引用。</summary>
    void OnReturnedToPool();
}
