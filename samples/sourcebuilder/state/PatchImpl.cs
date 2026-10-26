namespace ExamplePatch
{
    // This metadata-only extension type is rebound to Example.Calculator by the manifest.
    public class CalculatorState
    {
        public int Extra = 7;
        public int Bonus { get; set; } = 2;
    }

    public static class StatefulPatch
    {
        private static int calls;

        public static int Add(Example.Calculator self, int value)
        {
            var state = (CalculatorState)(object)self;
            state.Extra++;
            state.Bonus++;
            return self.Offset + value + state.Extra + state.Bonus + ++calls;
        }
    }
}
