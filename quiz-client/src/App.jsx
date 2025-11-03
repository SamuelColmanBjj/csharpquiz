import { useEffect, useState } from "react";
import Confetti from "react-confetti";
import { CountdownCircleTimer } from "react-countdown-circle-timer";

const WS_URL = "ws://localhost:5000/ws/";

export default function App() {
  const [ws, setWs] = useState(null);
  const [playerId, setPlayerId] = useState("");
  const [roomId, setRoomId] = useState("");
  const [questions, setQuestions] = useState([]);
  const [currentIndex, setCurrentIndex] = useState(0);
  const [showConfetti, setShowConfetti] = useState(false);
  const [score, setScore] = useState({});
  const [timerKey, setTimerKey] = useState(0);

  const playerName = "Jugador";

  // Conectar WebSocket
  useEffect(() => {
    const socket = new WebSocket(WS_URL);
    setWs(socket);

    socket.onopen = () => {
      console.log("Conectado al servidor");
      socket.send(JSON.stringify({ type: "join", name: playerName }));
    };

    socket.onmessage = (event) => {
      const data = JSON.parse(event.data);
      if (data.type === "room_assigned") {
        setPlayerId(data.playerIdEnc);
        setRoomId(data.roomId);
      } else if (data.type === "question") {
        setQuestions((prev) => [...prev, data]);
        setCurrentIndex(prev => prev + 1);
        setTimerKey(prev => prev + 1);
      } else if (data.type === "score_update") {
        setScore(data.scores);
      }
    };

    return () => socket.close();
  }, []);

  const handleAnswer = (answer) => {
    if (!ws) return;

    const currentQuestion = questions[currentIndex - 1];
    ws.send(JSON.stringify({
      type: "answer",
      playerIdEnc: playerId,
      answer: answer
    }));

    // Mostrar confetti si acierta
    if (currentQuestion.options.includes(answer)) { // opción simplificada
      setShowConfetti(true);
      setTimeout(() => setShowConfetti(false), 3000);
    }

    // pasar a siguiente pregunta
    setTimerKey(prev => prev + 1);
  };

  const currentQuestion = questions[currentIndex - 1];

  return (
    <div className="min-h-screen flex flex-col items-center justify-center bg-gradient-to-br from-purple-600 via-pink-500 to-orange-400 text-white p-4">
      {showConfetti && <Confetti />}
      <h1 className="text-4xl font-bold mb-4">Quiz Game</h1>
      <div className="mb-4">
        Score: {Object.entries(score).map(([name, s]) => `${name}: ${s} `)}
      </div>

      {currentQuestion ? (
        <div className="bg-black/50 p-6 rounded-lg w-full max-w-xl text-center space-y-4">
          <div className="text-2xl font-semibold">{currentQuestion.text}</div>

          <div className="grid grid-cols-2 gap-4 mt-4">
            {currentQuestion.options.map((opt, i) => (
              <button
                key={i}
                className="bg-white text-black py-2 px-4 rounded hover:scale-105 transition-transform"
                onClick={() => handleAnswer(opt)}
              >
                {opt}
              </button>
            ))}
          </div>

          <div className="mt-4 flex justify-center">
            <CountdownCircleTimer
              key={timerKey}
              isPlaying
              duration={15}
              size={80}
              strokeWidth={6}
              colors={[["#FF0000", 0.33], ["#FFA500", 0.33], ["#00FF00", 0.33]]}
              onComplete={() => setTimerKey(prev => prev + 1)}
            >
              {({ remainingTime }) => <span>{remainingTime}s</span>}
            </CountdownCircleTimer>
          </div>
        </div>
      ) : (
        <div className="text-xl">Esperando preguntas...</div>
      )}
    </div>
  );
}
