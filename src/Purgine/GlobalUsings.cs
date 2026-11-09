// Los proyectos WPF excluyen System.IO de los usings implícitos (choque con System.Windows.Shapes.Path).
// Aquí no usamos Shapes desde C#, así que lo importamos globalmente.
global using System.IO;
