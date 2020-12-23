namespace UpdateFieldCodeGenerator.Structures
{
    [HasChangesMask]
    public class JamMirrorQuestSession_C
    {
        public static readonly UpdateField owner = new UpdateField(typeof(WoWGuid), UpdateFieldFlag.None);
        public static readonly UpdateField questCompleted = new UpdateField(typeof(ulong[]), UpdateFieldFlag.None, 875);
    }
}
