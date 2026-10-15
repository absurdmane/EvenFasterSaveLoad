using System;
using FullSerializer;

namespace EvenFasterSaveLoad.Json
{
    /// <summary>Thrown when the fast path can't reproduce vanilla exactly; the caller then runs vanilla.</summary>
    internal sealed class FallbackException : Exception
    {
        public FallbackException(string reason) : base(reason) { }
    }

    internal static class FsHost
    {
        /// <summary>Set by the offline tests; in game the save system's own serializer instance is used.</summary>
        internal static fsSerializer Override;

        public static fsSerializer Serializer => Override ?? SaveController.JSON_serializer;
    }
}
