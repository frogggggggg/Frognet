using System;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Evaluates expressions over numbers and vectors, such as <c>here+(0,2,0)*3</c>. Operators are
/// componentwise and a number meeting a vector broadcasts, so <c>(1,2,3)+5</c> is <c>(6,7,8)</c>.
/// </summary>
public static class MathExpression
{
    /// <summary>A scalar is held in all three components, which is what makes broadcasting free.</summary>
    public readonly struct Value
    {
        public readonly bool isVector;
        public readonly Vector3 vector;

        public Value(Vector3 vector, bool isVector)
        {
            this.vector = vector;
            this.isVector = isVector;
        }

        public float scalar => vector.x;

        public override string ToString() =>
            isVector ? vector.ToString() : scalar.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <paramref name="lookup"/> resolves bare words to a float, int, Vector2 or Vector3,
    /// and returns null for anything it does not know.
    /// </summary>
    public static bool TryEvaluate(string expression, Func<string, object> lookup, out Value result, out string error)
    {
        result = default;
        error = null;

        try
        {
            var parser = new Parser(expression, lookup);
            result = parser.Expression();

            if (!parser.AtEnd)
                throw new ParseError($"Unexpected '{parser.Rest}' in '{expression}'");

            return true;
        }
        catch (ParseError failure)
        {
            error = failure.Message;
            return false;
        }
    }

    private class ParseError : Exception
    {
        public ParseError(string message) : base(message) { }
    }

    private class Parser
    {
        private readonly string text;
        private readonly Func<string, object> lookup;
        private int i;

        public Parser(string text, Func<string, object> lookup)
        {
            this.text = text ?? string.Empty;
            this.lookup = lookup;
        }

        public bool AtEnd => Peek() == '\0';
        public string Rest => text.Substring(i);

        private char Peek()
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            return i < text.Length ? text[i] : '\0';
        }

        private bool Take(char c)
        {
            if (Peek() != c)
                return false;

            i++;
            return true;
        }

        private void Expect(char c)
        {
            if (!Take(c))
                throw new ParseError($"Expected '{c}'");
        }

        public Value Expression()
        {
            Value left = Term();

            while (Peek() is '+' or '-')
                left = Apply(left, text[i++], Term());

            return left;
        }

        private Value Term()
        {
            Value left = Unary();

            while (Peek() is '*' or '/')
                left = Apply(left, text[i++], Unary());

            return left;
        }

        private Value Unary()
        {
            if (Take('-'))
            {
                Value value = Unary();
                return new Value(-value.vector, value.isVector);
            }

            Take('+');
            return Primary();
        }

        private Value Primary()
        {
            char c = Peek();

            if (c == '\0')
                throw new ParseError("Expression ends early");

            if (char.IsDigit(c) || c == '.')
                return Number(ReadNumber());

            if (char.IsLetter(c) || c == '_')
                return Resolve(ReadWord());

            if (Take('('))
                return Group();

            throw new ParseError($"Unexpected '{c}'");
        }

        private Value Group()
        {
            Value first = Expression();

            // A comma is the only thing separating a vector literal from ordinary grouping.
            if (!Take(','))
            {
                Expect(')');
                return first;
            }

            Value second = Expression();
            Expect(',');
            Value third = Expression();
            Expect(')');

            if (first.isVector || second.isVector || third.isVector)
                throw new ParseError("Vector components have to be numbers, not vectors");

            return new Value(new Vector3(first.scalar, second.scalar, third.scalar), true);
        }

        private float ReadNumber()
        {
            int start = i;

            while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                i++;

            string raw = text.Substring(start, i - start);

            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value
                : throw new ParseError($"'{raw}' is not a valid number");
        }

        private string ReadWord()
        {
            int start = i;

            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                i++;

            return text.Substring(start, i - start);
        }

        private Value Resolve(string word)
        {
            object named = lookup?.Invoke(word) ?? throw new ParseError($"Unknown word '{word}'");

            return named switch
            {
                float value => Number(value),
                int value => Number(value),
                double value => Number((float)value),
                Vector3 value => new Value(value, true),
                Vector2 value => new Value(value, true),
                _ => throw new ParseError($"'{word}' is a {named.GetType().Name}, not a number or vector")
            };
        }

        private static Value Number(float value) => new Value(new Vector3(value, value, value), false);

        private static Value Apply(Value a, char op, Value b)
        {
            Vector3 result = default;

            for (int k = 0; k < 3; k++)
            {
                if (op == '/' && b.vector[k] == 0f)
                    throw new ParseError("Division by zero");

                result[k] = op switch
                {
                    '+' => a.vector[k] + b.vector[k],
                    '-' => a.vector[k] - b.vector[k],
                    '*' => a.vector[k] * b.vector[k],
                    _ => a.vector[k] / b.vector[k]
                };
            }

            return new Value(result, a.isVector || b.isVector);
        }
    }
}
