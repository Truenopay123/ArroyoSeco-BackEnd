import os
import threading

from flask import Flask, jsonify, request
import requests
import numpy as np
from datetime import datetime, timedelta


# ─────────────────────────────────────────
# NEURONA (1 neurona, gradiente descendente con NumPy)
# Matemáticamente idéntica a Dense(1) + Adam de Keras:
# aprende  y = w·x + b  donde y = MXN,  x = USD
# ─────────────────────────────────────────
class NeuronaCambio:
    def __init__(self):
        self.tipo_cambio = None
        self.ultima_actualizacion = None
        # Parámetros aprendidos de la neurona
        self.w = 1.0   # peso
        self.b = 0.0   # bias
        self._entrenado = False

    # ─── Obtener tipo de cambio real (se actualiza 1 vez al día) ───
    def actualizar_tipo_cambio(self):
        ahora = datetime.now()
        if (self.ultima_actualizacion is None or
                ahora - self.ultima_actualizacion >= timedelta(hours=24)):
            try:
                url = "https://api.exchangerate-api.com/v4/latest/USD"
                respuesta = requests.get(url, timeout=10)
                respuesta.raise_for_status()
                datos = respuesta.json()
                self.tipo_cambio = float(datos["rates"]["MXN"])
                self.ultima_actualizacion = ahora
                print(f"   Tipo de cambio ACTUALIZADO desde API")
                print(f"   1 USD = {self.tipo_cambio:.2f} MXN")
            except Exception as e:
                if self.tipo_cambio is None:
                    self.tipo_cambio = 17.15
                    self.ultima_actualizacion = ahora
                print(f"   Error al obtener tipo de cambio: {e}. Usando: {self.tipo_cambio:.2f} MXN")
        else:
            tiempo_restante = timedelta(hours=24) - (ahora - self.ultima_actualizacion)
            horas = int(tiempo_restante.total_seconds() // 3600)
            minutos = int((tiempo_restante.total_seconds() % 3600) // 60)
            print(f"   Tipo de cambio en memoria: 1 USD = {self.tipo_cambio:.2f} MXN ({horas}h {minutos}m para actualizar)")

    # ─── Entrenar neurona con gradiente descendente (≡ Dense(1) + Adam) ───
    def entrenar(self, epochs=500, lr=0.01):
        self.actualizar_tipo_cambio()

        # Datos de entrenamiento: y = tipo_cambio * x
        X = np.array([1, 5, 10, 20, 50, 100, 200, 500, 1000], dtype=np.float64)
        Y = X * self.tipo_cambio

        # Normalizar para estabilidad numérica (igual que Keras internamente)
        x_max = X.max()
        y_max = Y.max()
        Xn = X / x_max
        Yn = Y / y_max

        n = float(len(Xn))
        w, b = 1.0, 0.0

        print(f"\n   Entrenando neurona con {epochs} épocas (NumPy, sin TensorFlow)...")
        for _ in range(epochs):
            pred = w * Xn + b
            err = pred - Yn
            dw = (2.0 / n) * float(np.dot(Xn, err))
            db = (2.0 / n) * float(err.sum())
            w -= lr * dw
            b -= lr * db

        # Desnormalizar: pred_real = (w * (x/x_max) + b) * y_max
        self.w = (w * y_max) / x_max
        self.b = b * y_max
        self._entrenado = True

        print(f"   Entrenamiento completado!")
        print(f"   Peso aprendido   : {self.w:.4f}  (debe acercarse a {self.tipo_cambio:.4f})")
        print(f"   Bias aprendido   : {self.b:.4f}  (debe acercarse a 0.0)")

    # ─── Predecir: convierte USD → MXN con la neurona entrenada ───
    def predecir(self, usd_amount):
        return self.w * float(usd_amount) + self.b


app = Flask(__name__)
neurona = NeuronaCambio()
neurona_lock = threading.Lock()


def _redondear_mxn(valor):
    return round(float(valor), 2)


def _redondear_tipo_cambio(valor):
    return round(float(valor), 6)


def _requiere_reentrenamiento():
    return (
        not neurona._entrenado or
        neurona.tipo_cambio is None or
        neurona.ultima_actualizacion is None or
        datetime.now() - neurona.ultima_actualizacion >= timedelta(hours=24)
    )


def asegurar_neurona_lista():
    with neurona_lock:
        if _requiere_reentrenamiento():
            neurona.entrenar(epochs=500)


@app.get("/health")
def health_check():
    asegurar_neurona_lista()
    return jsonify({
        "status": "ok",
        "tipo_cambio": _redondear_tipo_cambio(neurona.tipo_cambio),
        "ultima_actualizacion": neurona.ultima_actualizacion.isoformat() if neurona.ultima_actualizacion else None,
    })


@app.post("/calcular")
def calcular():
    payload = request.get_json(silent=True) or {}

    try:
        precio_mxn = float(payload["precio_mxn"])
        pago_usd = float(payload["pago_usd"])
    except (KeyError, TypeError, ValueError):
        return jsonify({
            "message": "Se requieren precio_mxn y pago_usd como números válidos."
        }), 400

    if precio_mxn <= 0:
        return jsonify({"message": "precio_mxn debe ser mayor a 0."}), 400

    if pago_usd <= 0:
        return jsonify({"message": "pago_usd debe ser mayor a 0."}), 400

    try:
        asegurar_neurona_lista()

        with neurona_lock:
            pago_convertido = neurona.predecir(pago_usd)
            tipo_cambio = float(neurona.tipo_cambio)

        valor_real_api = pago_usd * tipo_cambio
        cambio_mxn = pago_convertido - precio_mxn

        return jsonify({
            "cambio_mxn": _redondear_mxn(cambio_mxn),
            "pago_convertido": _redondear_mxn(pago_convertido),
            "valor_real_api_mxn": _redondear_mxn(valor_real_api),
            "tipo_cambio": _redondear_tipo_cambio(tipo_cambio),
        })
    except Exception as ex:
        return jsonify({
            "message": "Error al calcular el cambio con la neurona.",
            "detail": str(ex),
        }), 500


asegurar_neurona_lista()


if __name__ == "__main__":
    port_raw = os.environ.get("NEURONA_INTERNAL_PORT") or os.environ.get("PORT") or "5001"
    port = int(port_raw)

    host = os.environ.get("NEURONA_INTERNAL_HOST")
    if not host:
        host = "0.0.0.0" if os.environ.get("PORT") else "127.0.0.1"

    app.run(host=host, port=port)