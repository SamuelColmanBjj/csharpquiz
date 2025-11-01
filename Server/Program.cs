using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

class Program
{
    static List<Room> rooms = new List<Room>();
    static List<WebSocket> clients = new List<WebSocket>();
    static string secret = "MiSecretoSuperSeguro";

    static async Task Main()
    {
        InitializeDatabase();
        SeedDatabase();

        HttpListener listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:5000/ws/");
        listener.Start();
        Console.WriteLine("Servidor WebSocket escuchando en ws://localhost:5000/ws/ ...");

        while (true)
        {
            var context = await listener.GetContextAsync();

            if (context.Request.IsWebSocketRequest)
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                var webSocket = wsContext.WebSocket;

                clients.Add(webSocket);
                Console.WriteLine("Cliente conectado. Total clientes: " + clients.Count);

                _ = Task.Run(() => ReceiveMessages(webSocket));
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }

    // Inicializa la base de datos
    static void InitializeDatabase()
    {
        using var connection = new SqliteConnection("Data Source=quiz.db");
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Quiz (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Question (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                QuizId INTEGER NOT NULL,
                Text TEXT NOT NULL,
                CorrectAnswer TEXT NOT NULL,
                Options TEXT,
                FOREIGN KEY(QuizId) REFERENCES Quiz(Id)
            );
        ";
        cmd.ExecuteNonQuery();

        Console.WriteLine("Base de datos inicializada.");
    }

    // Inserta datos de prueba
    static void SeedDatabase()
    {
        using var connection = new SqliteConnection("Data Source=quiz.db");
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Quiz";
        long count = (long)cmd.ExecuteScalar()!;
        if (count > 0) return;

        cmd.CommandText = "INSERT INTO Quiz (Title) VALUES (@title)";
        cmd.Parameters.AddWithValue("@title", "Quiz de Prueba");
        cmd.ExecuteNonQuery();

        var cmdId = connection.CreateCommand();
        cmdId.CommandText = "SELECT last_insert_rowid();";
        long quizId = (long)cmdId.ExecuteScalar()!;

        cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO Question (QuizId, Text, CorrectAnswer, Options) VALUES (@quizId, @text, @answer, @options)";
        cmd.Parameters.AddWithValue("@quizId", quizId);

        cmd.Parameters.AddWithValue("@text", "Capital de Francia?");
        cmd.Parameters.AddWithValue("@answer", "paris");
        cmd.Parameters.AddWithValue("@options", "[\"paris\",\"londres\",\"berlin\",\"roma\"]");
        cmd.ExecuteNonQuery();

        cmd.Parameters["@text"].Value = "2 + 2?";
        cmd.Parameters["@answer"].Value = "4";
        cmd.Parameters["@options"].Value = "[\"3\",\"4\",\"5\",\"6\"]";
        cmd.ExecuteNonQuery();

        cmd.Parameters["@text"].Value = "Color del cielo?";
        cmd.Parameters["@answer"].Value = "azul";
        cmd.Parameters["@options"].Value = "[\"azul\",\"verde\",\"rojo\",\"amarillo\"]";
        cmd.ExecuteNonQuery();

        Console.WriteLine("Datos de prueba insertados.");
    }

    // Obtener preguntas de un quiz
    static List<(string Text, string CorrectAnswer, string[] Options)> GetQuestions(int quizId)
    {
        var questions = new List<(string, string, string[])>();

        using var connection = new SqliteConnection("Data Source=quiz.db");
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Text, CorrectAnswer, Options FROM Question WHERE QuizId = @quizId";
        cmd.Parameters.AddWithValue("@quizId", quizId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string text = reader.GetString(0);
            string answer = reader.GetString(1);
            string[] options = reader.IsDBNull(2) ? new string[0] : System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(2))!;
            questions.Add((text, answer, options));
        }

        return questions;
    }

    // Asignar jugador a sala
    static Room AssignRoom(Player player)
    {
        Room room = rooms.Find(r => r.Players.Count < r.MaxPlayers);

        if (room == null)
        {
            room = new Room { RoomId = Guid.NewGuid().ToString() };
            rooms.Add(room);
            Console.WriteLine("Se creó una nueva sala: " + room.RoomId);
        }

        room.Players.Add(player);
        Console.WriteLine($"Jugador {player.Name} se unió a la sala {room.RoomId}");

        // Inicia el quiz automáticamente para pruebas
        _ = Task.Run(() => StartQuiz(room));

        return room;
    }

    static string GeneratePlayerId(string playerName, string roomId)
    {
        var payload = $"{playerName}:{roomId}:{DateTime.UtcNow.Ticks}";
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    static async Task ReceiveMessages(WebSocket ws)
    {
        var buffer = new byte[1024 * 4];

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Cerrando", CancellationToken.None);
                clients.Remove(ws);

                foreach (var room in rooms)
                {
                    var p = room.Players.Find(pl => pl.Socket == ws);
                    if (p != null) room.Players.Remove(p);
                }

                Console.WriteLine("Cliente desconectado. Total clientes: " + clients.Count);
            }
            else
            {
                var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine("Recibido: " + msg);

                try
                {
                    var data = System.Text.Json.JsonDocument.Parse(msg).RootElement;
                    string type = data.GetProperty("type").GetString()!;

                    if (type == "join")
                    {
                        string playerName = data.GetProperty("name").GetString()!;
                        string roomIdTemp = Guid.NewGuid().ToString();
                        var player = new Player
                        {
                            Name = playerName,
                            PlayerIdEnc = GeneratePlayerId(playerName, roomIdTemp),
                            Socket = ws
                        };

                        var room = AssignRoom(player);

                        var response = new
                        {
                            type = "room_assigned",
                            roomId = room.RoomId,
                            playerIdEnc = player.PlayerIdEnc
                        };

                        string json = System.Text.Json.JsonSerializer.Serialize(response);
                        byte[] bufferResp = Encoding.UTF8.GetBytes(json);
                        await ws.SendAsync(new ArraySegment<byte>(bufferResp), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    else if (type == "answer")
                    {
                        string playerIdEnc = data.GetProperty("playerIdEnc").GetString()!;
                        string answer = data.GetProperty("answer").GetString()!;

                        Player player = null!;
                        Room room = null!;

                        foreach (var r in rooms)
                        {
                            player = r.Players.Find(p => p.PlayerIdEnc == playerIdEnc)!;
                            if (player != null)
                            {
                                room = r;
                                break;
                            }
                        }

                        if (player != null && room != null)
                        {
                            var questions = GetQuestions(1); // Quiz de prueba
                            var currentQuestion = questions.FirstOrDefault();
                            if (currentQuestion.Text.ToLower() == answer.ToLower())
                            {
                                player.Score += 1;
                            }

                            var scoreMsg = new
                            {
                                type = "score_update",
                                scores = room.Players.ToDictionary(p => p.Name, p => p.Score)
                            };

                            string jsonScore = System.Text.Json.JsonSerializer.Serialize(scoreMsg);
                            byte[] scoreBuffer = Encoding.UTF8.GetBytes(jsonScore);

                            foreach (var p in room.Players)
                            {
                                if (p.Socket.State == WebSocketState.Open)
                                {
                                    await p.Socket.SendAsync(new ArraySegment<byte>(scoreBuffer), WebSocketMessageType.Text, true, CancellationToken.None);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error procesando mensaje: " + ex.Message);
                }
            }
        }
    }

    static async Task SendQuestionToRoom(Room room, string questionText, string[] options)
    {
        var question = new
        {
            type = "question",
            text = questionText,
            options = options,
            questionId = Guid.NewGuid().ToString()
        };
        string json = System.Text.Json.JsonSerializer.Serialize(question);
        byte[] buffer = Encoding.UTF8.GetBytes(json);

        foreach (var player in room.Players)
        {
            if (player.Socket.State == WebSocketState.Open)
            {
                await player.Socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }

    static async Task StartQuiz(Room room)
    {
        var questions = GetQuestions(1); // Quiz de prueba
        foreach (var q in questions)
        {
            await SendQuestionToRoom(room, q.Text, q.Options);
            Console.WriteLine($"Pregunta enviada a sala {room.RoomId}: {q.Text}");
            await Task.Delay(15000); // 15 segundos por pregunta
        }

        Console.WriteLine($"Quiz terminado en sala {room.RoomId}");
    }
}

class Player
{
    public string Name { get; set; } = string.Empty;
    public string PlayerIdEnc { get; set; } = string.Empty;
    public WebSocket Socket { get; set; } = null!;
    public int Score { get; set; } = 0;
}

class Room
{
    public string RoomId { get; set; } = string.Empty;
    public List<Player> Players { get; set; } = new List<Player>();
    public int MaxPlayers { get; set; } = 5;
}
