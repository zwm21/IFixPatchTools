namespace ExamplePatch
{
    public static class PatchImpl
    {
        public static int Add(Example.Calculator self, int value)
        {
            return Normalize(self.Offset + value);
        }

        private static int Normalize(int value) => value < 0 ? 0 : value;
    }
}
