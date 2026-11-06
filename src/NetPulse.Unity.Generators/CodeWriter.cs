using System.Text;

namespace NetPulse.Unity.Generators
{
    internal sealed class CodeWriter
    {
        private readonly StringBuilder _builder = new StringBuilder();
        private int _indent;

        public int Counter { get; private set; }

        public int Next() => ++Counter;

        public void Line(string text = "")
        {
            if (text.Length > 0)
            {
                _builder.Append(' ', _indent * 4);
                _builder.Append(text);
            }

            _builder.Append('\n');
        }

        public void Open(string header)
        {
            if (header.Length > 0)
            {
                Line(header);
            }

            Line("{");
            _indent++;
        }

        public void Close(string suffix = "")
        {
            _indent--;
            Line("}" + suffix);
        }

        public void Raw(string text)
        {
            _builder.Append(text);
            _builder.Append('\n');
        }

        public override string ToString() => _builder.ToString();
    }
}
