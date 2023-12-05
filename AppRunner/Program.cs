
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using AppRunner;

class Program
{
    private const string START_LINE = "-------------Start------------";
    private const string STOP_LINE = "-------------Stop------------";
    
    public static void Main(string[] args)
    {
        // Небольшое вступление как я понял коды возврата - 
        // OK - файл успешно запущен;
        // ERRORS - не смог запуститься (поменяли что-то в бинарном файле);
        // INVALID_SYNTAX - /path [ничего не написали] /trace ...;
        // COMMAND_NOT_SUPPORTED - даны неверные параметры - лишние или неверные (/path123);
        // FAIL - другая ошибка (не существует).
        
        string file = "";
        string mode = "";
        string logFile = "";
        
        // Программа не логирует ошибки INVALID_SYNTAX и COMMAND_NOT_SUPPORTED
        // потому что как можно залоггировать какую-то информацию, если программа даже не смогла
        // запуститься (ошибка при старте), а не во время исполнения.
        Console.WriteLine(START_LINE);

        // проверка на валидность синтаксиса
        if (!CheckIsValidSyntax(args))
        {
            Console.WriteLine(Constants.ERROR_SYNTAX);
            Console.WriteLine(STOP_LINE);
            return;
        }
        
        // проверка на то что указаны поддерживаемые параметры
        if (CheckCommandNotSupported(args))
        {
            // выводим в консоль
            Console.WriteLine(Constants.ERROR_COMMAND);
            Console.WriteLine(STOP_LINE);
            return;
        }

        // булевый флаг что программа будет логгировать
        bool isLogActive = false;

        // обработка того, что в лог файл нельзя записывать (стоит запрет на запись (конечно, странно, но прога должна отрабатывать всегда и не
        // ложиться с ошибкой))
        try
        {
            // проверка, что нужно логгировать
            if (args.Contains("/trace"))
            {
                isLogActive = true;
                logFile = args[3];
                // если в пути есть директории, которых не существует создаем их
                var directory = Path.GetDirectoryName(logFile);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // создаем лог файл
                if (!File.Exists(logFile))
                {
                    using (File.Create(logFile))
                    {
                    }
                }

                File.AppendAllText(logFile, START_LINE + '\n');
            }
        }
        catch
        {
            Console.WriteLine(Constants.OTHER_FAIL + ":" + logFile);
            Console.WriteLine(STOP_LINE);
            return;
        }


        mode = args[0];
        file = args[1];
        
        List<string> files = new List<string>();

        // обработчик ошибок при записи (в лог файл добавляется строка START) или чтении информации из файла
        try
        {
            // получаем наши исполняемые файлы либо в одиночном
            // либо в пакетном режиме
            // в зависимости от флага /path и /lst
            if (mode == "/path" && File.Exists(file))
            {
                files.Add(file);
            }
            // исполняемого файла не существует => не можем его запустить
            else if (mode == "/path" && !File.Exists(file))
            {
                // логгируем если был передан параметр
                if (isLogActive)
                {
                    LogResultToFile(file, logFile, Constants.OTHER_FAIL);
                    File.AppendAllText(logFile, STOP_LINE + '\n');
                }

                Console.WriteLine(Constants.OTHER_FAIL + ":" + file);
                Console.WriteLine($"TOTAL - {Constants.OTHER_FAIL}");
                Console.WriteLine(STOP_LINE);
                return;
            }
            // Передан пакетный режим
            else
            {
                // логгируем если был передан параметр, но самого исполняемого файла нет
                if (isLogActive && !File.Exists(file))
                {
                    LogResultToFile(file, logFile, Constants.OTHER_FAIL);
                    File.AppendAllText(logFile, STOP_LINE + '\n');
                }
                // выводим в консоль что нет исполняемого файла (не был передан флаг /trace)
                if (!File.Exists(file))
                {
                    Console.WriteLine(Constants.OTHER_FAIL + ":" + file);
                    Console.WriteLine(STOP_LINE);
                    return;
                }

                // обработка файла, содержащего исполняемые файлы
                foreach (var line in File.ReadAllLines(path: file))
                {
                    // если исполняемый файл содержит пробелы, то обрабатываем это
                    if (line.Contains(' '))
                    {
                        StringBuilder st = new StringBuilder();
                        foreach (var lineElement in line)
                        {
                            if (lineElement != '"')
                            {
                                st.Append(lineElement);
                            }
                        }

                        files.Add(st.ToString());
                    }
                    else
                    {
                        files.Add(line);
                    }
                }
            }
        }
        catch
        {
            Console.WriteLine(Constants.OTHER_FAIL);
            Console.WriteLine(STOP_LINE);
            return;
        }

        // запускаем наши задачи
        List<Task<string>> tasks = new List<Task<string>>();
        foreach (var executionFile in files)
        {
            tasks.Add(Task.Run(() => RunFile(executionFile, logFile, isLogActive)));    
        }

        // Одна или более программ отработали на ERRORS – утилита вернёт ERRORS,
        // но предварительно запустит все оставшиеся программы;
        
        // Если хоть одна программа отработает на FAIL, то утилита вернёт FAIL,
        // но предварительно запустит все оставшиеся программы.
        
        // Тк утилита возвращает одно значение в итог, пусть 
        // Приоритет будет таковым: 1) ERRORS   2) FAIL

        string resultUtil = Constants.OK;

        var isTimeLimit = false;
        // запускаем основной поток, который может работать 20_000 сек
        var mainTask = Task.Run(() =>
        {
            // проходимся асинхронно по всем уже запустившимся задачам
            Parallel.ForEach(tasks, (Task task, ParallelLoopState breaker) =>
            {
                // Ждем 10_000 сек, если процесс запускается больше, чем это время
                // то утилита завершается досрочно => завершаются все остальные потоки вместе с текущим
                task.Wait(10000000);
                if (task.Status == TaskStatus.Running)
                {
                    isTimeLimit = true;
                    breaker.Break();
                }
            });
        });
        
        // ждем пока выполнятся все задачи
        mainTask.Wait(20000000);
        // превышен лимит времени исполнения хотя бы 1 из задач
        if (isTimeLimit)
        {
            // логгируем если есть флаг /trace
            if (isLogActive)
            {
                File.AppendAllText(logFile, "Превышен максимальный лимит запуска для одного процесса\n" +
                                            $"{STOP_LINE} + \n");
            }
            Console.WriteLine("Превышен максимальный лимит запуска для одного процесса");
            Console.WriteLine(STOP_LINE);
            return;
        }
        // работа самой улиты больше 20_000 сек
        if (mainTask.Status == TaskStatus.Running)
        {
            // логгируем если бы передан флаг /trace
            if (isLogActive)
            {
                File.AppendAllText(logFile, "Превышен максимальный лимит запуска для утилиты\n" +
                                            $"{STOP_LINE} + \n");
            }
            Console.WriteLine("Превышен максимальный лимит запуска для утилиты");
            Console.WriteLine(STOP_LINE);
            return;
        }
        
        // проходимся по результатам задач
        for (int i = 0; i < tasks.Count; ++i)
        {
            if (tasks[i].Result == Constants.ERROR_LAUNCH)
            {
                resultUtil = Constants.ERROR_LAUNCH;
            }

            if (tasks[i].Result == Constants.OTHER_FAIL && resultUtil == Constants.OK)
            {
                resultUtil = Constants.OTHER_FAIL;
            }
            
            Console.WriteLine((tasks[i].Result == Constants.OK ? Constants.OK : Constants.OTHER_FAIL) + ":" + GetOnlyNameOfProgram(files[i]));
        }

        if (isLogActive)
        {
            File.AppendAllText(logFile, $"TOTAL - {resultUtil}\n");
            File.AppendAllText(logFile, STOP_LINE + '\n');
        }

        Console.WriteLine($"TOTAL - {resultUtil}");
        Console.WriteLine(STOP_LINE);
    }

    /// <summary>
    /// Запускает исполняемый файл
    /// </summary>
    /// <param name="path"></param>
    /// <param name="logFile"></param>
    /// <param name="isLogActive"></param>
    /// <returns></returns>
    public static string RunFile(string path, string logFile, bool isLogActive)
    {
        // проверка на то что файл существует
        if (!File.Exists(path))
        {
            // включено логгирование
            if (isLogActive)
            {
                LogResultToFile(path, logFile, Constants.OTHER_FAIL);
            }
           return Constants.OTHER_FAIL;
        }

        // обработка ошибки при запуске исполняемого файла
        try
        {
            // запускаем исполняемый файл
            var process = Process.Start(path);

            // запустился без ошибок
            if (process != null && !process.HasExited)
            {
                if (isLogActive)
                {
                    LogResultToFile(path, logFile, Constants.OK);
                }

                return Constants.OK;
            }
        }
        // произошло исключение при запуске файла
        catch
        {
            // логируем, если был передан флаг /trace
            if (isLogActive)
            {
                // Из задания
                // <результат_запуска>:<программа>
                // Где результат_запуска – это либо OK (код возврата OK) или FAIL (коды возврата ERRORS или FAIL).
                // здесь ошибка ERRORS но логировать надо как FAIL, как я понял.
                LogResultToFile(path, logFile, Constants.OTHER_FAIL);
            }
        }
        
        return Constants.ERROR_LAUNCH;
    }
    
    private static object _lockObj = new object();
    public static void LogResultToFile(string pathExeFile, string logFile, string result)
    {
        string nameProgram = GetOnlyNameOfProgram(pathExeFile);
        // логируем дату запуска файла / результат / программу
        // добавлен lock, потому что несколько потоков могут иметь дело с одним файлом из-за этого может вылезти исключение
        lock (_lockObj)
        {
            File.AppendAllText(logFile, DateTime.Now + " >>> " + result + ":" + nameProgram + "\n");
        }
    }

    /// <summary>
    /// Получаем короткое название программы, без полного пути (для красоты логгирования)
    /// </summary>
    /// <param name="fullProgramName"></param>
    /// <returns></returns>
    public static string GetOnlyNameOfProgram(string fullProgramName)
    {
        int indexOfLastSlash = fullProgramName.LastIndexOf('/');
        // Собираем только название исполняемого файла (для удобства)
        StringBuilder nameProgram = new StringBuilder();
        for (int i = indexOfLastSlash + 1; i < fullProgramName.Length; ++i)
        {
            nameProgram.Append(fullProgramName[i]);
        }
        
        return nameProgram.ToString();
    }
    
    
    /// <summary>
    /// Проверка валидноcть введенной команды
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    public static bool CheckIsValidSyntax(string[] args)
    {
        return ((args.Length == 2 && (args[0] == "/path" || args[0] == "/lst")) 
                || (args.Length == 4 && (args[0] == "/path" || args[0] == "/lst") && args[2] == "/trace"));
    }

    /// <summary>
    /// Проверка на валидность параметров
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    public static bool CheckCommandNotSupported(string[] args)
    {
        // передано 1 или более 4 параметров, следовательно, это не будет поддерживаться
        if (args.Length <= 1 || args.Length >= 5)
        {
            return true;
        }

        // неправильный флаг для указания режима (одиночный или пакетный)
        if (args[0] != "/lst" && args[0] != "/path")
        {
            return true;
        }

        // неправильный флаг для указания логгирования
        if (args.Length == 4 && args[2] != "/trace")
        {
            return true;
        }

        return false;
    }
}

