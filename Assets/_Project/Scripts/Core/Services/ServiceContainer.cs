using System;
using System.Collections.Generic;

namespace Skyrift.Core.Services
{
    /// <summary>
    /// Composition-time dependency container (TECH_DOC §4.0/§4.1, ADR-0003).
    /// Only Skyrift.Bootstrap may call Register. Consumers receive dependencies through Construct(...)
    /// and must never call Resolve per frame.
    /// </summary>
    public interface IServiceContainer
    {
        void Register<T>(T instance) where T : class;
        void RegisterLazy<T>(Func<T> factory) where T : class;
        T Resolve<T>() where T : class;
        bool TryResolve<T>(out T service) where T : class;

        /// <summary>Child scope that falls back to its parent. Dispose releases only the scope's own services.</summary>
        IServiceContainer CreateScope();
    }

    /// <summary>
    /// Dictionary-backed container. No reflection, no attribute scanning, IL2CPP-safe.
    /// Resolution after boot is allocation-free (a dictionary lookup on a reference type).
    /// </summary>
    public sealed class ServiceContainer : IServiceContainer, IDisposable
    {
        private readonly Dictionary<Type, object> _instances = new Dictionary<Type, object>(64);
        private readonly Dictionary<Type, Func<object>> _factories = new Dictionary<Type, Func<object>>(16);
        private readonly ServiceContainer _parent;
        private bool _disposed;

        public ServiceContainer(ServiceContainer parent = null)
        {
            _parent = parent;
        }

        public void Register<T>(T instance) where T : class
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            ThrowIfDisposed();

            Type type = typeof(T);
            if (_instances.ContainsKey(type) || _factories.ContainsKey(type))
            {
                throw new InvalidOperationException(
                    $"Service '{type.Name}' is already registered in this scope. Register once, in Bootstrap.");
            }

            _instances[type] = instance;
        }

        public void RegisterLazy<T>(Func<T> factory) where T : class
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            ThrowIfDisposed();

            Type type = typeof(T);
            if (_instances.ContainsKey(type) || _factories.ContainsKey(type))
            {
                throw new InvalidOperationException($"Service '{type.Name}' is already registered in this scope.");
            }

            _factories[type] = () => factory();
        }

        public T Resolve<T>() where T : class
        {
            if (TryResolve<T>(out T service)) return service;
            throw new InvalidOperationException(
                $"Service '{typeof(T).Name}' is not registered. Bootstrap order is defined in TECH_DOC §4.1.");
        }

        public bool TryResolve<T>(out T service) where T : class
        {
            ThrowIfDisposed();
            Type type = typeof(T);

            if (_instances.TryGetValue(type, out object existing))
            {
                service = (T)existing;
                return true;
            }

            if (_factories.TryGetValue(type, out Func<object> factory))
            {
                object created = factory();
                _factories.Remove(type);
                _instances[type] = created;
                service = (T)created;
                return true;
            }

            if (_parent != null) return _parent.TryResolve(out service);

            service = null;
            return false;
        }

        public IServiceContainer CreateScope()
        {
            ThrowIfDisposed();
            return new ServiceContainer(this);
        }

        /// <summary>Releases IDisposable services owned by this scope (not the parent's).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (KeyValuePair<Type, object> pair in _instances)
            {
                if (pair.Value is IDisposable disposable) disposable.Dispose();
            }

            _instances.Clear();
            _factories.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ServiceContainer));
        }
    }
}
