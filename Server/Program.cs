using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static List<Room> rooms = new List<Room>();
    static List<WebSocket> clients = new List<WebSocket>();
    static string secret = "MiSecretoSuperSeguro"; // secreto para playerIdEnc

    static async Task Main()
    {
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

        // Si la sala está llena, iniciar quiz automáticamente
        if (room.Players.Count == room.MaxPlayers)
        {
            _ = Task.Run(() => StartQuiz(room));
        }

        return room;
    }

    static string GeneratePlayerId(string playerName, string roomId)
    {
        var payload = $"{playerName}:{roomId}:{DateTime.UtcNow.Ticks}";
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
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

                // También eliminar de salas
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
                    string type = data.GetProperty("type").GetString();

                    if (type == "join")
                    {
                        string playerName = data.GetProperty("name").GetString();
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
                        string playerIdEnc = data.GetProperty("playerIdEnc").GetString();
                        string answer = data.GetProperty("answer").GetString();

                        Player player = null;
                        Room room = null;

                        foreach (var r in rooms)
                        {
                            player = r.Players.Find(p => p.PlayerIdEnc == playerIdEnc);
                            if (player != null)
                            {
                                room = r;
                                break;
                            }
                        }

                        if (player != null && room != null)
                        {
                            // Validación simple: si la respuesta es "b" sumamos 1 punto
                            if (answer.ToLower() == "b") player.Score += 1;

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

    static async Task BroadcastMessage(string message, WebSocket sender)
    {
        var msgBuffer = Encoding.UTF8.GetBytes(message);

        foreach (var client in clients)
        {
            if (client.State == WebSocketState.Open && client != sender)
            {
                await client.SendAsync(new ArraySegment<byte>(msgBuffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }

    static async Task SendQuestionToRoom(Room room, string questionText)
    {
        var question = new { type = "question", text = questionText, questionId = Guid.NewGuid().ToString() };
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
        string[] questions = { "Capital de Francia?", "2 + 2?", "Color del cielo?" };
        string[] correctAnswers = { "paris", "4", "azul" };

        for (int i = 0; i < questions.Length; i++)
        {
            await SendQuestionToRoom(room, questions[i]);
            Console.WriteLine($"Pregunta enviada a sala {room.RoomId}: {questions[i]}");
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
    public string RoomId { get; set; }
    public List<Player> Players { get; set; } = new List<Player>();
    public int MaxPlayers { get; set; } = 5;
}
