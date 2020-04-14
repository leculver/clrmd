using System;
using System.Runtime.InteropServices;
#pragma warning disable 0169
#pragma warning disable 0414

public static class SharedStaticTest
{
    public static int Value;
}

public class Foo
{
    int i = 42;
    string s = "string";
    bool b = true;
    float f = 4.2f;
    double d = 8.4;
    object o = new object();
    Struct st = new Struct(12);
    GenericClass<bool, int, float, string, object> g = new GenericClass<bool, int, float, string, object>();

    public string FooString = "Foo string";

    public void Bar() { }
    public void Baz() { }
    public int Baz(int i) { return i; }
    public T5 GenericBar<T1, T2, T3, T4, T5>(GenericClass<T1, T2, T3, T4, T5> a) { return a.Invoke(default(T1), default(T2), default(T3), default(T4)); }
}

[StructLayout(LayoutKind.Sequential)]
public struct Struct
{
    int j;
    Struct2 struct2;
    string s;
    object o;

    public Struct(int i)
    {
        j = i;
        struct2 = new Struct2() { value = 13 };
        s = "string2";
        o = new object();
    }
}

public struct Struct2
{
    public int value;
}

public class GenericClass<T1, T2, T3, T4, T5>
{
    public T5 Invoke(T1 a, T2 b, T3 te, T4 t4) { return default(T5); }
}

struct EmptyStruct { }
struct NestedEmptyStruct { EmptyStruct es; }
public class StructTestClass { Struct s; NestedEmptyStruct nes; }
