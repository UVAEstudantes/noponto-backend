- endpoint que retorna o itinerario ida e volta junto por linha

- botar um bool nesses endpoints pra view de paradas

- filtro de sentido no itinerario

- tem dois endpoints q retornam as linhas q passam em uma parada (da pra transformar um deles em tempo ate o proximo veiculo)

- melhorar o nome da linha pra ser apenas o "855" e com a desc de "Terminal Magarça <-> Terminal Deodoro"

- melhorar o linhas detalhes pra mostrar valor passagem

- sistema de alertas/notificações com signalR para informar de próximos ônibus

- melhorar veiculos em tempo real

- get count pois por itinerario, retornar itinerarioId e count, sort de quantidade


problemas com o GPS:

- veiculos somem e surgem em posições aleatorias da linha e dps voltam pra posição real

- esta pegando veiculos "fantasmas", provavelmente veiculos q ficaram sem gps horas atras (eles ficam em loop na tela)

- ainda erra muito na distancia percorrida durante o tempo sem dados da API

- um erro absurdo q eu percebi é quando uso o endpoint de itinerario pra mapear a via e isso muda a direção de alguns veiculos