namespace NLoop.Server


module Constants =
  type private Foo = Bar
  let AssemblyVersion =
    Bar.GetType().Assembly.GetName().Version.ToString()
